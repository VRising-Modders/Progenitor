using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Progenitor.Native;

/// <summary>
/// Stable function ids and per-build ids for the address library. Relative addresses move every
/// game patch; the game's metadata <c>Type$$Method</c> name does not, so detours look up a name instead of a
/// hard-coded offset. The build id is the loaded GameAssembly.dll's size, PE TimeDateStamp,
/// PE CheckSum, and a SHA-256 of the first <see cref="BuildIdPrefixBytes"/> bytes so two
/// same-size images with different PE stamps cannot share a table name, and the first
/// <see cref="AddressLibrary.TryResolve"/> does not hash ~236 MB on the calling thread.
/// Two images that share size, stamp, checksum, and the first 64 KiB still collide; that is
/// the prefix-hash trade-off. A leftover JSON with that colliding id is refused unless the
/// live module's file length and PE checksum match the fields already in the id, and a
/// resolved relative address must lie in an executable section before a JMP (otherwise prefix fallback).
/// </summary>
public static class AddressId
{
    /// <summary>
    /// Preferred image base of this game's 64-bit PE as recorded in the game's metadata.
    /// Method-address listing <c>Address</c> values below this are already relative addresses;
    /// a value at or above is a VA.
    /// </summary>
    public const long DefaultImageBase = 0x180000000;

    /// <summary>
    /// Client custom-event receive. Bootstrap <c>ReceiveDetours</c> and Core's
    /// Bootstrap-absent fallback both detour this. Unique in the game's metadata (no overload suffix).
    /// </summary>
    public const string ClientOnReliableEventsReceived =
        "ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived";

    /// <summary>
    /// Server custom-event receive. Unique in the game's metadata (no overload suffix).
    /// </summary>
    public const string ServerOnDataReceivedServer =
        "ProjectM.ServerBootstrapSystem$$OnDataReceivedServer";

    /// <summary>
    /// Observe-only FindEntry on the managed-data dictionary
    /// (<c>PrefabLookupKey</c> → object). The metadata name keeps the generic arguments
    /// and drops the <c>Stunlock.Core.</c> prefix on the key type.
    /// </summary>
    public const string ManagedDataFindEntry =
        "System.Collections.Generic.Dictionary<PrefabLookupKey, object>$$FindEntry";

    /// <summary>Ping / log key for the client receive detour.</summary>
    public const string SiteReceiveClient = "receive.client";

    /// <summary>Ping / log key for the server receive detour.</summary>
    public const string SiteReceiveServer = "receive.server";

    /// <summary>Ping / log key for the managed-data FindEntry probe.</summary>
    public const string SiteKeyProbeFindEntry = "keyprobe.findentry";

    /// <summary>
    /// Metadata method name of the live hosted-child spawn body. The abstract
    /// <c>StunProcess$$Start</c> is not the site that runs.
    /// </summary>
    public const string StunProcessMonoStart = "StunProcess_MONO$$Start";

    /// <summary><c>IMAGE_SCN_MEM_EXECUTE</c>. A JMP target must land in a section with this bit.</summary>
    public const uint ImageScnMemExecute = 0x20000000;

    /// <summary>Resolved from the address-library JSON for the running GameAssembly build.</summary>
    public const string PathTable = "table";

    /// <summary>
    /// Table for this build was missing, so <c>MethodPointerByPrefix</c> supplied the pointer.
    /// Not used when a table is loaded but the id is absent — that is <see cref="PathMissing"/>.
    /// </summary>
    public const string PathFallback = "fallback";

    /// <summary>Neither the table nor the prefix fallback produced a pointer; the detour is not installed.</summary>
    public const string PathMissing = "missing";

    /// <summary>Every native detour site Core and Bootstrap resolve. Wanted-list and ping both walk this.</summary>
    public static readonly DetourSiteInfo[] DetourSites =
    {
        new(SiteReceiveClient, ClientOnReliableEventsReceived),
        new(SiteReceiveServer, ServerOnDataReceivedServer),
        new(SiteKeyProbeFindEntry, ManagedDataFindEntry),
    };

    /// <summary>Hex characters of the SHA-256 kept in a build id (8 bytes).</summary>
    public const int Sha256PrefixHexChars = 16;

    /// <summary>
    /// Bytes hashed from the start of GameAssembly.dll (and of the generator's copy).
    /// 64 KiB covers the DOS/PE headers (TimeDateStamp, CheckSum) and enough of the
    /// image that a typical patch changes the prefix hash without reading the rest of
    /// the file. Stamp and checksum are also mixed into the id as their own fields.
    /// </summary>
    public const int BuildIdPrefixBytes = 65536;

    /// <summary>
    /// Turn a metadata address into a relative address. The game's method-address listing stores
    /// relative addresses (native tools add the program image base); a VA that still includes
    /// <paramref name="imageBase"/> is subtracted so either flavor lands on the same number.
    /// </summary>
    public static long ToImageOffset(long address, long imageBase)
    {
        if (imageBase > 0 && address >= imageBase) return address - imageBase;
        return address;
    }

    /// <summary>
    /// Assign unique ids treating <paramref name="methods"/> as the whole metadata listing. A name
    /// that appears once is the id (generics and nested types are already in the metadata name).
    /// A name that appears more than once appends <c>#</c> plus its TypeSignature; a leftover
    /// collision appends <c>#2</c>, <c>#3</c>, … in listing order. Call this on the full
    /// ScriptMethod list, then drop ids you do not want to ship — assigning after a wanted-list
    /// filter would give a unique name a bare id that later grows a <c>#TypeSignature</c> suffix
    /// when another overload is added. The algorithm is duplicated in
    /// <c>tools/addresslib/build.mjs</c> and must stay in lockstep.
    /// </summary>
    public static IReadOnlyList<AssignedEntry> Assign(IReadOnlyList<DumpMethod> methods)
    {
        if (methods == null || methods.Count == 0) return Array.Empty<AssignedEntry>();

        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < methods.Count; i++)
        {
            var name = methods[i].Name ?? "";
            if (!byName.TryGetValue(name, out var list))
            {
                list = new List<int>();
                byName[name] = list;
            }
            list.Add(i);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new AssignedEntry[methods.Count];
        foreach (var pair in byName)
        {
            var group = pair.Value;
            var disambiguate = group.Count > 1;
            foreach (var index in group)
            {
                var row = methods[index];
                var id = disambiguate
                    ? pair.Key + "#" + (row.TypeSignature ?? "")
                    : pair.Key;
                if (!used.Add(id))
                {
                    var n = 2;
                    var candidate = id + "#" + n.ToString(CultureInfo.InvariantCulture);
                    while (!used.Add(candidate))
                    {
                        n++;
                        candidate = id + "#" + n.ToString(CultureInfo.InvariantCulture);
                    }
                    id = candidate;
                }
                assigned[index] = new AssignedEntry(id, row.ImageOffset);
            }
        }
        return assigned;
    }

    /// <summary>
    /// Assign ids against the full metadata listing, then keep only the wanted rows. A method that has
    /// overloads in <paramref name="allMethods"/> but is the only hit in
    /// <paramref name="wanted"/> still gets the <c>#TypeSignature</c> suffix, so expanding the
    /// wanted list later does not rename it.
    /// </summary>
    public static IReadOnlyList<AssignedEntry> AssignWanted(
        IReadOnlyList<DumpMethod> allMethods,
        IReadOnlyList<DumpMethod> wanted)
    {
        if (wanted == null || wanted.Count == 0) return Array.Empty<AssignedEntry>();
        if (allMethods == null || allMethods.Count == 0) return Array.Empty<AssignedEntry>();

        var assigned = Assign(allMethods);
        var byKey = new Dictionary<DumpKey, string>();
        for (var i = 0; i < allMethods.Count; i++)
        {
            var row = allMethods[i];
            var key = new DumpKey(row.Name, row.TypeSignature, row.ImageOffset);
            if (!byKey.ContainsKey(key))
                byKey[key] = assigned[i].Id;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AssignedEntry>(wanted.Count);
        for (var i = 0; i < wanted.Count; i++)
        {
            var row = wanted[i];
            if (!byKey.TryGetValue(new DumpKey(row.Name, row.TypeSignature, row.ImageOffset), out var id))
                continue;
            if (!seen.Add(id)) continue;
            result.Add(new AssignedEntry(id, row.ImageOffset));
        }
        return result;
    }

    /// <summary>
    /// <c>{fileSize}-{timeDateStamp}-{checkSum}-{sha256-prefix}</c> in invariant decimal (size)
    /// and lowercase hex (the PE fields and the hash). Stamp and checksum are 0 when the bytes
    /// are not a PE. The hash prefix length is <see cref="Sha256PrefixHexChars"/> so the id
    /// stays short in file names. Must stay in lockstep with <c>tools/addresslib/build.mjs</c>.
    /// </summary>
    public static string ComputeBuildId(
        long fileSize,
        ReadOnlySpan<byte> sha256,
        uint timeDateStamp = 0,
        uint checkSum = 0)
    {
        var hex = Convert.ToHexString(sha256).ToLowerInvariant();
        if (hex.Length > Sha256PrefixHexChars)
            hex = hex.Substring(0, Sha256PrefixHexChars);
        return fileSize.ToString(CultureInfo.InvariantCulture)
            + "-" + timeDateStamp.ToString("x", CultureInfo.InvariantCulture)
            + "-" + checkSum.ToString("x", CultureInfo.InvariantCulture)
            + "-" + hex;
    }

    /// <summary>
    /// Hash at most <see cref="BuildIdPrefixBytes"/> from the current position of
    /// <paramref name="stream"/> and format <see cref="ComputeBuildId(long, ReadOnlySpan{byte})"/>.
    /// Does not rewind; does not read past the prefix even if the stream is the whole DLL.
    /// </summary>
    public static string ComputeBuildId(Stream stream, long fileSize)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        var prefix = ReadPrefix(stream, fileSize);
        return ComputeBuildIdFromPrefix(prefix, fileSize);
    }

    /// <summary>
    /// Size, PE TimeDateStamp, PE CheckSum, and SHA-256 of the first
    /// <see cref="BuildIdPrefixBytes"/> of a file on disk. Used by the generator and by the
    /// live loader; both must produce the same string.
    /// </summary>
    public static string ComputeBuildIdFromFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required.", nameof(path));
        var size = new FileInfo(path).Length;
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: Math.Min(BuildIdPrefixBytes, 64 * 1024),
            FileOptions.SequentialScan);
        return ComputeBuildId(fs, size);
    }

    /// <summary>
    /// Same id <see cref="ComputeBuildIdFromFile"/> would produce from <paramref name="prefix"/>
    /// (the first bytes of the file, at most <see cref="BuildIdPrefixBytes"/>).
    /// </summary>
    public static string ComputeBuildIdFromPrefix(ReadOnlySpan<byte> prefix, long fileSize)
    {
        TryReadPeIdentifiers(prefix, out var timeDateStamp, out var checkSum);
        var hash = SHA256.HashData(prefix);
        return ComputeBuildId(fileSize, hash, timeDateStamp, checkSum);
    }

    /// <summary>
    /// Split <c>{fileSize}-{stamp}-{checkSum}-{sha}</c> back into fields. False when the
    /// string is not four dash-separated tokens or the numbers do not parse.
    /// </summary>
    public static bool TryParseBuildId(
        string? buildId,
        out long fileSize,
        out uint timeDateStamp,
        out uint checkSum,
        out string shaPrefix)
    {
        fileSize = 0;
        timeDateStamp = 0;
        checkSum = 0;
        shaPrefix = "";
        if (string.IsNullOrEmpty(buildId)) return false;
        var parts = buildId.Split('-');
        if (parts.Length != 4) return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSize))
            return false;
        if (fileSize < 0) return false;
        if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out timeDateStamp))
            return false;
        if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out checkSum))
            return false;
        shaPrefix = parts[3] ?? "";
        return shaPrefix.Length == Sha256PrefixHexChars;
    }

    /// <summary>
    /// True when <paramref name="buildId"/> names this module's size and PE checksum.
    /// A 64 KiB-prefix collision that also reused a leftover JSON still has to match
    /// the live file length and the checksum already stored in the id.
    /// </summary>
    public static bool BuildIdMatchesModule(string? buildId, long fileLength, uint peCheckSum)
    {
        if (!TryParseBuildId(buildId, out var size, out _, out var sum, out _))
            return false;
        if (size != fileLength) return false;
        return sum == peCheckSum;
    }

    /// <summary>
    /// PE section table from a file prefix. Empty when the bytes are not a PE or the
    /// table does not fit. Used to refuse a JMP whose relative address is outside executable sections.
    /// </summary>
    public static bool TryReadPeSections(ReadOnlySpan<byte> prefix, out PeSection[] sections)
    {
        sections = Array.Empty<PeSection>();
        if (prefix.Length < 0x40) return false;
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z') return false;
        int pe = ReadInt32(prefix, 0x3C);
        if (pe < 0 || pe + 24 > prefix.Length) return false;
        if (prefix[pe] != (byte)'P' || prefix[pe + 1] != (byte)'E' || prefix[pe + 2] != 0 || prefix[pe + 3] != 0)
            return false;
        int coff = pe + 4;
        if (coff + 20 > prefix.Length) return false;
        int numberOfSections = prefix[coff + 2] | (prefix[coff + 3] << 8);
        int sizeOfOptional = prefix[coff + 16] | (prefix[coff + 17] << 8);
        if (numberOfSections <= 0 || numberOfSections > 96) return false;
        int optional = coff + 20;
        int sectionTable = optional + sizeOfOptional;
        int sectionBytes = numberOfSections * 40;
        if (sectionTable < 0 || sectionTable + sectionBytes > prefix.Length) return false;
        var list = new PeSection[numberOfSections];
        for (int i = 0; i < numberOfSections; i++)
        {
            int row = sectionTable + (i * 40);
            uint virtualSize = ReadUInt32(prefix, row + 8);
            uint virtualAddress = ReadUInt32(prefix, row + 12);
            uint sizeOfRaw = ReadUInt32(prefix, row + 16);
            uint characteristics = ReadUInt32(prefix, row + 36);
            list[i] = new PeSection(virtualAddress, virtualSize, sizeOfRaw, characteristics);
        }
        sections = list;
        return true;
    }

    /// <summary>
    /// True when <paramref name="imageOffset"/> lies in a section with <see cref="ImageScnMemExecute"/>.
    /// No sections means the caller could not parse the PE: do not JMP from that uncertainty
    /// here (the caller decides whether to skip the check).
    /// </summary>
    public static bool ImageOffsetLiesInExecutableSection(long imageOffset, ReadOnlySpan<PeSection> sections)
    {
        if (imageOffset < 0 || sections.Length == 0) return false;
        ulong u = (ulong)imageOffset;
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            if ((s.Characteristics & ImageScnMemExecute) == 0) continue;
            uint span = s.VirtualSize > s.SizeOfRawData ? s.VirtualSize : s.SizeOfRawData;
            if (span == 0) continue;
            if (u < s.VirtualAddress) continue;
            if (u - s.VirtualAddress >= span) continue;
            return true;
        }
        return false;
    }

    /// <summary>Grep fragment when a disk or embedded table attaches: <c>source=disk path=… build=…</c>.</summary>
    public static string FormatTableSource(string source, string? path, string build) =>
        "source=" + (source ?? "")
        + (string.IsNullOrEmpty(path) ? "" : " path=" + path)
        + " build=" + (build ?? "");

    /// <summary>Readable line when a table relative address is in-module but not in .text / an executable section.</summary>
    public static string FormatImageOffsetNotExecutable(string site, string id, long imageOffset) =>
        "site=" + (site ?? "")
        + " id=" + (id ?? "")
        + " offset=0x" + imageOffset.ToString("X", CultureInfo.InvariantCulture)
        + " is not in an executable section; falling back to prefix";

    /// <summary>
    /// PE COFF TimeDateStamp and optional-header CheckSum from a file prefix. False when the
    /// bytes are not a PE (stamp and checksum then stay 0 and the build id still hashes the
    /// prefix as opaque data). Mixed into <see cref="ComputeBuildId"/> so two same-size PE
    /// files with different timestamps cannot share an id even if a caller forgot to hash.
    /// </summary>
    public static bool TryReadPeIdentifiers(ReadOnlySpan<byte> prefix, out uint timeDateStamp, out uint checkSum)
    {
        timeDateStamp = 0;
        checkSum = 0;
        if (prefix.Length < 0x40) return false;
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z') return false;

        int pe = ReadInt32(prefix, 0x3C);
        if (pe < 0 || pe + 88 + 4 > prefix.Length) return false;
        if (prefix[pe] != (byte)'P' || prefix[pe + 1] != (byte)'E' || prefix[pe + 2] != 0 || prefix[pe + 3] != 0)
            return false;

        timeDateStamp = ReadUInt32(prefix, pe + 8);
        // Optional header starts at PE+24; CheckSum is at optional-header offset 64 for PE32 and PE32+.
        int optional = pe + 24;
        if (optional + 66 > prefix.Length) return false;
        checkSum = ReadUInt32(prefix, optional + 64);
        return true;
    }

    private static byte[] ReadPrefix(Stream stream, long fileSize)
    {
        var n = fileSize < 0 ? 0 : fileSize;
        if (n > BuildIdPrefixBytes) n = BuildIdPrefixBytes;
        var buffer = new byte[(int)n];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0) break;
            read += got;
        }
        if (read == buffer.Length) return buffer;
        if (read == 0) return Array.Empty<byte>();
        var trimmed = new byte[read];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
        return trimmed;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => (uint)ReadInt32(data, offset);

    /// <summary>
    /// Exact rebuild command the missing-table error names so an operator does not have to
    /// guess the side. Lockstep with <c>tools/addresslib/build.mjs</c> usage text.
    /// </summary>
    public static string FormatRebuildCommand(string side) =>
        "node tools/addresslib/build.mjs --side " + NormalizeSide(side);

    /// <summary>
    /// One log line per detour site: which path produced the pointer and the relative address.
    /// AddressLibrary prefixes <c>[addresslib] </c>; Bootstrap prefixes <c>ReceiveDetours: </c>.
    /// </summary>
    public static string FormatResolvedLine(string site, string id, string path, long imageOffset, IntPtr address) =>
        "site=" + (site ?? "")
        + " id=" + (id ?? "")
        + " path=" + (path ?? "")
        + " offset=0x" + imageOffset.ToString("X", CultureInfo.InvariantCulture)
        + " address=0x" + ((long)address).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>
    /// The one readable error when a detour is not installed. Names the site, the running
    /// build id, and the generator command. <paramref name="tablePresent"/> distinguishes
    /// "no JSON for this GameAssembly" from "JSON loaded but this id is absent".
    /// </summary>
    public static string FormatNotInstalled(string site, string id, string side, string build, bool tablePresent)
    {
        side = NormalizeSide(side);
        build = string.IsNullOrEmpty(build) ? "(unknown)" : build;
        site ??= "";
        id ??= "";
        if (tablePresent)
        {
            return "Address library table for " + side + " build " + build
                + " has no relative address for site '" + site + "' (" + id
                + "). The detour was not installed. Rebuild with: "
                + FormatRebuildCommand(side);
        }

        return "Address library has no table for " + side + " build " + build
            + ". Site '" + site + "' (" + id + ") was not installed. Rebuild with: "
            + FormatRebuildCommand(side);
    }

    /// <summary>One-line ping/status fragment: build id, whether a table attached, per-site path.</summary>
    public static string FormatPingLine(
        string side,
        string build,
        bool tablePresent,
        IReadOnlyList<DetourSiteOutcome>? sites)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("native side=").Append(NormalizeSide(side));
        sb.Append(" build=").Append(string.IsNullOrEmpty(build) ? "(unknown)" : build);
        sb.Append(" table=").Append(tablePresent ? "yes" : "no");
        sb.Append(" sites=");
        if (sites == null || sites.Count == 0)
        {
            sb.Append("(none)");
            return sb.ToString();
        }

        for (var i = 0; i < sites.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var s = sites[i];
            sb.Append(s.Site).Append(':').Append(s.Path);
            if (s.ImageOffset != 0)
                sb.Append("@0x").Append(s.ImageOffset.ToString("X", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    internal static string NormalizeSide(string? side) =>
        string.Equals(side, "server", StringComparison.OrdinalIgnoreCase) ? "server" : "client";

    private readonly struct DumpKey : IEquatable<DumpKey>
    {
        public DumpKey(string name, string typeSignature, long imageOffset)
        {
            Name = name ?? "";
            TypeSignature = typeSignature ?? "";
            ImageOffset = imageOffset;
        }

        public string Name { get; }
        public string TypeSignature { get; }
        public long ImageOffset { get; }

        public bool Equals(DumpKey other) =>
            ImageOffset == other.ImageOffset
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(TypeSignature, other.TypeSignature, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is DumpKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = Name.GetHashCode();
                h = (h * 397) ^ TypeSignature.GetHashCode();
                h = (h * 397) ^ ImageOffset.GetHashCode();
                return h;
            }
        }
    }
}

/// <summary>One ScriptMethod row of the game's metadata after relative-address conversion, before id assignment.</summary>
public readonly struct DumpMethod
{
    public DumpMethod(string name, string typeSignature, long imageOffset)
    {
        Name = name ?? "";
        TypeSignature = typeSignature ?? "";
        ImageOffset = imageOffset;
    }

    public string Name { get; }
    public string TypeSignature { get; }
    public long ImageOffset { get; }
}

/// <summary>A stable id and the relative address it maps to in one game build.</summary>
public readonly struct AssignedEntry
{
    public AssignedEntry(string id, long imageOffset)
    {
        Id = id ?? "";
        ImageOffset = imageOffset;
    }

    public string Id { get; }
    public long ImageOffset { get; }
}

/// <summary>One PE section used to sanity-check a detour relative address before the JMP.</summary>
public readonly struct PeSection
{
    public PeSection(uint virtualAddress, uint virtualSize, uint sizeOfRawData, uint characteristics)
    {
        VirtualAddress = virtualAddress;
        VirtualSize = virtualSize;
        SizeOfRawData = sizeOfRawData;
        Characteristics = characteristics;
    }

    public uint VirtualAddress { get; }
    public uint VirtualSize { get; }
    public uint SizeOfRawData { get; }
    public uint Characteristics { get; }

    public bool IsExecutable => (Characteristics & AddressId.ImageScnMemExecute) != 0;
}

/// <summary>One native detour site: ping key plus the metadata method name it resolves.</summary>
public readonly struct DetourSiteInfo
{
    public DetourSiteInfo(string site, string id)
    {
        Site = site ?? "";
        Id = id ?? "";
    }

    public string Site { get; }
    public string Id { get; }
}

/// <summary>
/// Recorded outcome of resolving one detour site. <see cref="Path"/> is
/// <see cref="AddressId.PathTable"/>, <see cref="AddressId.PathFallback"/>, or
/// <see cref="AddressId.PathMissing"/>.
/// </summary>
public readonly struct DetourSiteOutcome
{
    public DetourSiteOutcome(string site, string id, string path, long imageOffset, IntPtr address)
    {
        Site = site ?? "";
        Id = id ?? "";
        Path = path ?? "";
        ImageOffset = imageOffset;
        Address = address;
    }

    public string Site { get; }
    public string Id { get; }
    public string Path { get; }
    public long ImageOffset { get; }
    public IntPtr Address { get; }
}

/// <summary>
/// Ping/status <c>native</c> object. Build id of the running GameAssembly, whether a matching
/// table attached, and each attempted detour site's resolve path. Lives here so
/// <see cref="AddressLibrary"/> compiles without <c>DetourSite.cs</c>.
/// </summary>
public sealed class NativePingSnapshot
{
    public NativePingSnapshot(
        string side,
        string build,
        bool tablePresent,
        string status,
        IReadOnlyList<DetourSiteOutcome> sites)
    {
        Side = side ?? "";
        Build = build ?? "";
        TablePresent = tablePresent;
        Status = status ?? "";
        Sites = sites ?? Array.Empty<DetourSiteOutcome>();
        Line = AddressId.FormatPingLine(Side, Build, TablePresent, Sites);
        var reports = new NativePingSiteReport[Sites.Count];
        for (var i = 0; i < Sites.Count; i++)
        {
            var s = Sites[i];
            reports[i] = new NativePingSiteReport(s.Site, s.Id, s.Path, s.ImageOffset);
        }
        Reports = reports;
    }

    public string Side { get; }
    public string Build { get; }
    public bool TablePresent { get; }
    public string Status { get; }
    public IReadOnlyList<DetourSiteOutcome> Sites { get; }

    /// <summary>Same as <see cref="Sites"/> without native pointers, for ping/status JSON.</summary>
    public IReadOnlyList<NativePingSiteReport> Reports { get; }

    /// <summary>Grep line: <c>native side=… build=… table=yes|no sites=…</c>.</summary>
    public string Line { get; }

    /// <summary>
    /// Anonymous-object payload Dev (or any caller) can serialize without mapping IntPtr.
    /// Shape: side, build, tablePresent, status, line, sites[].
    /// </summary>
    public object ToStatus() => new
    {
        side = Side,
        build = Build,
        tablePresent = TablePresent,
        status = Status,
        line = Line,
        sites = Reports,
    };
}

/// <summary>One detour site in a ping/status report (no native pointer).</summary>
public readonly struct NativePingSiteReport
{
    public NativePingSiteReport(string site, string id, string path, long imageOffset)
    {
        Site = site ?? "";
        Id = id ?? "";
        Path = path ?? "";
        Offset = imageOffset;
    }

    public string Site { get; }
    public string Id { get; }
    public string Path { get; }

    /// <summary>Image offset of the resolved target (serialized as <c>offset</c> in ping/status).</summary>
    public long Offset { get; }
}
