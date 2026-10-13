using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Progenitor.Install;

/// <summary>REMOTEINST1: what a remote install request asks the connected server to do. One byte on the wire.</summary>
internal enum RemoteInstallOp : byte
{
    None = 0,

    /// <summary>Install one Thunderstore package version that the server's ledger does not carry yet.</summary>
    Install = 1,

    /// <summary>Install a different version of a package the server's ledger already carries.</summary>
    Update = 2,

    /// <summary>Take back a ledgered install (only ledger files are ever removed).</summary>
    Uninstall = 3,

    /// <summary>Read-only: the server's remote-install status and, for an allowed admin, its ledger rows.</summary>
    List = 4,

    /// <summary>Cancel the operation in flight, if it is still downloading or verifying.</summary>
    Cancel = 5,
}

/// <summary>REMOTEINST1: where one operation is. The same states the local Install row passes through.</summary>
internal enum RemoteInstallStage : byte
{
    None = 0,
    Accepted = 1,
    Downloading = 2,
    Verifying = 3,
    Installing = 4,

    /// <summary>Terminal: written to the server's reload folder and loaded by its reloader.</summary>
    HotLoaded = 5,

    /// <summary>Terminal: written for the server's next start (an install, an update or an uninstall mark).</summary>
    PendingRestart = 6,

    /// <summary>Terminal: the files are gone now (a pending copy canceled, or a hot copy unloaded).</summary>
    Removed = 7,

    /// <summary>Terminal: refused before anything was written. <see cref="RemoteInstallReason"/> says why.</summary>
    Refused = 8,

    /// <summary>Terminal: accepted, then failed. Nothing new is ledgered for a failed install.</summary>
    Failed = 9,

    /// <summary>Terminal: the admin canceled it before anything was written.</summary>
    Cancelled = 10,
}

/// <summary>REMOTEINST1: the named reason behind a refusal or a failure. One byte on the wire.</summary>
internal enum RemoteInstallReason : byte
{
    None = 0,
    NotAdmin = 1,
    Disabled = 2,
    NotAllowed = 3,
    BadPackage = 4,
    BadVersion = 5,
    RateLimited = 6,
    Busy = 7,
    NotThunderstore = 8,
    NotFound = 9,
    VersionNotFound = 10,
    NotVRising = 11,
    ThunderstoreUnreachable = 12,
    DownloadFailed = 13,
    VerifyFailed = 14,
    PlanRefused = 15,
    InstallFailed = 16,
    NotInstalled = 17,
    AlreadyInstalled = 18,
    HasDependents = 19,
    Unavailable = 20,
    HotLoadUnconfirmed = 21,
    Internal = 22,
}

/// <summary>REMOTEINST1: tri-state of the server's Thunderstore reachability probe.</summary>
internal enum RemoteReachability : byte
{
    Unknown = 0,
    Reachable = 1,
    Unreachable = 2,
}

/// <summary>Client → server.</summary>
internal sealed class RemoteInstallRequest
{
    public RemoteInstallRequest(int nonce, RemoteInstallOp op, string package, string version, bool force = false)
    {
        Nonce = nonce;
        Op = op;
        Package = package ?? "";
        Version = version ?? "";
        Force = force;
    }

    public int Nonce { get; }
    public RemoteInstallOp Op { get; }

    /// <summary>Thunderstore <c>Namespace-Name</c>. Never a URL.</summary>
    public string Package { get; }

    /// <summary>Exact <c>major.minor.patch</c>. Empty for uninstall, list and cancel.</summary>
    public string Version { get; }

    /// <summary>Uninstall only: remove even though another ledgered install depends on it.</summary>
    public bool Force { get; }
}

/// <summary>Server → client: one step of one operation.</summary>
internal sealed class RemoteInstallProgress
{
    public RemoteInstallProgress(
        int nonce,
        RemoteInstallOp op,
        RemoteInstallStage stage,
        RemoteInstallReason reason,
        string package,
        string version,
        string detail)
    {
        Nonce = nonce;
        Op = op;
        Stage = stage;
        Reason = reason;
        Package = package ?? "";
        Version = version ?? "";
        Detail = detail ?? "";
    }

    public int Nonce { get; }
    public RemoteInstallOp Op { get; }
    public RemoteInstallStage Stage { get; }
    public RemoteInstallReason Reason { get; }
    public string Package { get; }
    public string Version { get; }

    /// <summary>A short plain sentence (the sub-cause of a network failure, a refusal's detail).</summary>
    public string Detail { get; }

    public bool IsTerminal => RemoteInstallWire.IsTerminal(Stage);
}

/// <summary>REMOTEINST1: what the server's ledger says about one package.</summary>
internal enum RemoteRowState : byte
{
    None = 0,

    /// <summary>Loaded at the server's last start (promoted from pending) — an uninstall is a mark for the next start.</summary>
    Loaded = 1,

    /// <summary>Written to the reload folder by the running server and loaded there.</summary>
    Hot = 2,

    /// <summary>Waiting in the pending folder for the next start.</summary>
    Pending = 3,

    /// <summary>Marked for removal at the next start.</summary>
    UninstallPending = 4,
}

/// <summary>One ledgered package on the server.</summary>
internal sealed class RemoteServerRow
{
    public RemoteServerRow(string package, string version, RemoteRowState state, string route)
    {
        Package = package ?? "";
        Version = version ?? "";
        State = state;
        Route = route ?? "";
    }

    public string Package { get; }
    public string Version { get; }
    public RemoteRowState State { get; }

    /// <summary><see cref="ServerInstallLedger.RouteRemote"/> when a remote install wrote it; empty for the local folder route and older rows.</summary>
    public string Route { get; }
}

/// <summary>Server → client: what this server offers this user, and (for an allowed admin) its ledger.</summary>
internal sealed class RemoteInstallStatus
{
    public RemoteInstallStatus(
        int nonce,
        bool enabled,
        bool authorized,
        bool allowed,
        RemoteReachability reachability,
        string reachabilityDetail,
        bool rowsIncluded,
        IReadOnlyList<RemoteServerRow>? rows,
        bool rowsTruncated = false,
        ulong steamId = 0,
        RemoteAuthorizedBy authorizedBy = RemoteAuthorizedBy.None,
        bool onAdminList = false,
        bool adminAuthed = false)
    {
        OnAdminList = onAdminList;
        AdminAuthed = adminAuthed;
        SteamId = steamId;
        Nonce = nonce;
        Enabled = enabled;
        Authorized = authorized;
        AuthorizedBy = authorized ? (authorizedBy == RemoteAuthorizedBy.None ? RemoteAuthorizedBy.AdminAuth : authorizedBy) : RemoteAuthorizedBy.None;
        Allowed = allowed;
        Reachability = reachability;
        ReachabilityDetail = reachabilityDetail ?? "";
        RowsIncluded = rowsIncluded;
        Rows = rows ?? Array.Empty<RemoteServerRow>();
        RowsTruncated = rowsTruncated;
    }

    /// <summary>0 for the unsolicited push after the handshake.</summary>
    public int Nonce { get; }
    public bool Enabled { get; }

    /// <summary>On the server's admin list, or authenticated with adminauth (the same rule the server applies).</summary>
    public bool Authorized { get; }

    /// <summary>Which of the two made <see cref="Authorized"/> true.</summary>
    public RemoteAuthorizedBy AuthorizedBy { get; }

    /// <summary>Alias of <see cref="Authorized"/> for one release.</summary>
    public bool Admin => Authorized;

    /// <summary>The receiving player's SteamID is in the server's admin list file.</summary>
    public bool OnAdminList { get; }

    /// <summary>The receiving player ran adminauth (<c>User.IsAdmin</c>): the gate other admin features use.</summary>
    public bool AdminAuthed { get; }

    /// <summary>Admin, enabled and inside <c>[RemoteInstall] AllowedSteamIds</c>: this user may install here.</summary>
    public bool Allowed { get; }

    public RemoteReachability Reachability { get; }

    /// <summary>The sub-cause of an unreachable probe (<c>dns</c>, <c>timeout</c>, …), empty otherwise.</summary>
    public string ReachabilityDetail { get; }

    /// <summary>False for a non-admin: rows are an admin-only read.</summary>
    public bool RowsIncluded { get; }

    public IReadOnlyList<RemoteServerRow> Rows { get; }
    public bool RowsTruncated { get; }

    /// <summary>
    /// The SteamID the server resolved for the requesting connection (its own <c>User.PlatformId</c>
    /// read). The client has no proven read of its own SteamID, so this is where the local
    /// admin-list helper learns it.
    /// </summary>
    public ulong SteamId { get; }
}

/// <summary>
/// REMOTEINST1: the wire of <see cref="Networking.CustomEventPolicy.RemoteInstallEventId"/>. One id
/// carries both directions; every packet starts with a version byte and a direction byte, so a
/// reply can never be decoded as a request (the variable-length payloads rule out the size trick
/// <see cref="StopServerReply"/> uses). Strings are length-prefixed UTF-8 with a hard cap; invalid
/// UTF-8, truncation, an unknown enum value and trailing bytes all reject the whole packet.
/// </summary>
internal static class RemoteInstallWire
{
    /// <summary>2: the status carries why the requester is authorized (admin list or adminauth).</summary>
    public const byte Version = 2;
    public const byte DirectionRequest = 0;
    public const byte DirectionProgress = 1;
    public const byte DirectionStatus = 2;

    public const int MaxPackageChars = 128;
    public const int MaxVersionChars = 32;
    public const int MaxDetailBytes = 512;

    /// <summary>Rows per status packet. 15 KiB payload cap ÷ (two short strings + two bytes) keeps far below it.</summary>
    public const int MaxRows = 64;

    public const int MaxPacketBytes = 14 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Thunderstore namespaces and names are letters, digits and underscores; the version is
    // strict semver core (Thunderstore enforces major.minor.patch on upload).
    private static readonly Regex PackagePattern = new("^[A-Za-z0-9_]{1,64}-[A-Za-z0-9_]{1,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant);

    public static bool IsValidPackage(string? package) =>
        !string.IsNullOrEmpty(package) && package!.Length <= MaxPackageChars && PackagePattern.IsMatch(package);

    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrEmpty(version) && version!.Length <= MaxVersionChars && VersionPattern.IsMatch(version);

    public static bool IsTerminal(RemoteInstallStage stage) =>
        stage is RemoteInstallStage.HotLoaded or RemoteInstallStage.PendingRestart or RemoteInstallStage.Removed
            or RemoteInstallStage.Refused or RemoteInstallStage.Failed or RemoteInstallStage.Cancelled;

    // ---- encode ----------------------------------------------------------------------------

    public static byte[] EncodeRequest(RemoteInstallRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return Write(DirectionRequest, w =>
        {
            w.Write((byte)request.Op);
            w.Write((byte)(request.Force ? 1 : 0));
            w.Write(request.Nonce);
            WriteText(w, request.Package, MaxPackageChars);
            WriteText(w, request.Version, MaxVersionChars);
        });
    }

    public static byte[] EncodeProgress(RemoteInstallProgress progress)
    {
        if (progress == null) throw new ArgumentNullException(nameof(progress));
        return Write(DirectionProgress, w =>
        {
            w.Write((byte)progress.Op);
            w.Write((byte)progress.Stage);
            w.Write((byte)progress.Reason);
            w.Write(progress.Nonce);
            WriteText(w, progress.Package, MaxPackageChars);
            WriteText(w, progress.Version, MaxVersionChars);
            WriteText(w, Clip(progress.Detail, MaxDetailBytes), MaxDetailBytes);
        });
    }

    public static byte[] EncodeStatus(RemoteInstallStatus status)
    {
        if (status == null) throw new ArgumentNullException(nameof(status));
        return Write(DirectionStatus, w =>
        {
            byte flags = 0;
            if (status.Enabled) flags |= 1;
            if (status.Authorized) flags |= 2;
            if (status.Allowed) flags |= 4;
            if (status.RowsIncluded) flags |= 8;
            bool truncated = status.RowsTruncated || status.Rows.Count > MaxRows;
            if (truncated) flags |= 16;
            if (status.OnAdminList) flags |= 32;
            if (status.AdminAuthed) flags |= 64;
            w.Write(flags);
            w.Write((byte)status.Reachability);
            // The host of a private game travels as AdminAuth ("an admin there"): older clients
            // refuse a status whose byte is past AdminAuth.
            w.Write((byte)(status.AuthorizedBy == RemoteAuthorizedBy.Host ? RemoteAuthorizedBy.AdminAuth : status.AuthorizedBy));
            w.Write(status.Nonce);
            w.Write(status.SteamId);
            WriteText(w, Clip(status.ReachabilityDetail, MaxDetailBytes), MaxDetailBytes);
            int count = status.RowsIncluded ? Math.Min(status.Rows.Count, MaxRows) : 0;
            w.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                var row = status.Rows[i];
                WriteText(w, row.Package, MaxPackageChars);
                WriteText(w, row.Version, MaxVersionChars);
                w.Write((byte)row.State);
                w.Write((byte)(string.Equals(row.Route, ServerInstallLedger.RouteRemote, StringComparison.Ordinal) ? 1 : 0));
            }
        });
    }

    // ---- decode ----------------------------------------------------------------------------

    /// <summary>The direction byte of a packet, or -1 when it is not this protocol's version.</summary>
    public static int DirectionOf(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && packet[0] == Version ? packet[1] : -1;

    public static bool TryDecodeRequest(ReadOnlySpan<byte> packet, out RemoteInstallRequest request)
    {
        request = null!;
        RemoteInstallRequest? decoded = null;
        bool ok = TryRead(packet, DirectionRequest, r =>
        {
            byte op = r.ReadByte();
            if (op < (byte)RemoteInstallOp.Install || op > (byte)RemoteInstallOp.Cancel) return false;
            byte force = r.ReadByte();
            if (force > 1) return false;
            int nonce = r.ReadInt32();
            string package = ReadText(r, MaxPackageChars);
            string version = ReadText(r, MaxVersionChars);
            decoded = new RemoteInstallRequest(nonce, (RemoteInstallOp)op, package, version, force == 1);
            return true;
        });
        if (!ok || decoded == null) return false;
        request = decoded;
        return true;
    }

    public static bool TryDecodeProgress(ReadOnlySpan<byte> packet, out RemoteInstallProgress progress)
    {
        progress = null!;
        RemoteInstallProgress? decoded = null;
        bool ok = TryRead(packet, DirectionProgress, r =>
        {
            byte op = r.ReadByte();
            byte stage = r.ReadByte();
            byte reason = r.ReadByte();
            if (op > (byte)RemoteInstallOp.Cancel || stage > (byte)RemoteInstallStage.Cancelled
                || reason > (byte)RemoteInstallReason.Internal) return false;
            int nonce = r.ReadInt32();
            string package = ReadText(r, MaxPackageChars);
            string version = ReadText(r, MaxVersionChars);
            string detail = ReadText(r, MaxDetailBytes);
            decoded = new RemoteInstallProgress(nonce, (RemoteInstallOp)op, (RemoteInstallStage)stage,
                (RemoteInstallReason)reason, package, version, detail);
            return true;
        });
        if (!ok || decoded == null) return false;
        progress = decoded;
        return true;
    }

    public static bool TryDecodeStatus(ReadOnlySpan<byte> packet, out RemoteInstallStatus status)
    {
        status = null!;
        RemoteInstallStatus? decoded = null;
        bool ok = TryRead(packet, DirectionStatus, r =>
        {
            byte flags = r.ReadByte();
            if ((flags & ~127) != 0) return false;
            byte reach = r.ReadByte();
            if (reach > (byte)RemoteReachability.Unreachable) return false;
            byte by = r.ReadByte();
            if (by > (byte)RemoteAuthorizedBy.Host) return false;
            int nonce = r.ReadInt32();
            ulong steamId = r.ReadUInt64();
            string detail = ReadText(r, MaxDetailBytes);
            int count = r.ReadByte();
            if (count > MaxRows) return false;
            bool included = (flags & 8) != 0;
            if (!included && count != 0) return false;
            var rows = new List<RemoteServerRow>(count);
            for (int i = 0; i < count; i++)
            {
                string package = ReadText(r, MaxPackageChars);
                string version = ReadText(r, MaxVersionChars);
                byte state = r.ReadByte();
                byte route = r.ReadByte();
                if (state > (byte)RemoteRowState.UninstallPending || route > 1) return false;
                rows.Add(new RemoteServerRow(package, version, (RemoteRowState)state, route == 1 ? ServerInstallLedger.RouteRemote : ""));
            }

            decoded = new RemoteInstallStatus(nonce, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                (RemoteReachability)reach, detail, included, rows, (flags & 16) != 0, steamId, (RemoteAuthorizedBy)by,
                (flags & 32) != 0, (flags & 64) != 0);
            return true;
        });
        if (!ok || decoded == null) return false;
        status = decoded;
        return true;
    }

    // ---- plumbing --------------------------------------------------------------------------

    private static byte[] Write(byte direction, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(direction);
            body(writer);
        }

        if (stream.Length > MaxPacketBytes) throw new ArgumentException("Remote install packet is too large.");
        return stream.ToArray();
    }

    private static bool TryRead(ReadOnlySpan<byte> packet, byte direction, Func<BinaryReader, bool> body)
    {
        if (packet.Length < 2 || packet.Length > MaxPacketBytes) return false;
        if (packet[0] != Version || packet[1] != direction) return false;
        try
        {
            using var stream = new MemoryStream(packet.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8);
            reader.ReadByte();
            reader.ReadByte();
            if (!body(reader)) return false;
            return stream.Position == stream.Length;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteText(BinaryWriter writer, string? text, int maxBytes)
    {
        byte[] bytes = Utf8.GetBytes(text ?? "");
        if (bytes.Length > maxBytes) throw new ArgumentException("A remote install field exceeds " + maxBytes + " UTF-8 bytes.");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int maxBytes)
    {
        int count = reader.ReadUInt16();
        if (count > maxBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid remote install field length.");
        return Utf8.GetString(reader.ReadBytes(count));
    }

    /// <summary>Cut a detail sentence to fit the field on a character boundary.</summary>
    internal static string Clip(string? text, int maxBytes)
    {
        string s = text ?? "";
        if (Utf8.GetByteCount(s) <= maxBytes) return s;
        var sb = new StringBuilder();
        int used = 0;
        foreach (char c in s)
        {
            int n = Utf8.GetByteCount(c.ToString());
            if (char.IsSurrogate(c)) break;
            if (used + n > maxBytes - 3) break;
            sb.Append(c);
            used += n;
        }

        return sb.Append("...").ToString();
    }

    /// <summary>The kebab-case name of a reason, for the audit log and the readback.</summary>
    public static string ReasonName(RemoteInstallReason reason) => reason switch
    {
        RemoteInstallReason.NotAdmin => "not-admin",
        RemoteInstallReason.Disabled => "disabled",
        RemoteInstallReason.NotAllowed => "not-allowed",
        RemoteInstallReason.BadPackage => "bad-package",
        RemoteInstallReason.BadVersion => "bad-version",
        RemoteInstallReason.RateLimited => "rate-limited",
        RemoteInstallReason.Busy => "busy",
        RemoteInstallReason.NotThunderstore => "not-thunderstore",
        RemoteInstallReason.NotFound => "not-found",
        RemoteInstallReason.VersionNotFound => "version-not-found",
        RemoteInstallReason.NotVRising => "not-v-rising",
        RemoteInstallReason.ThunderstoreUnreachable => "thunderstore-unreachable",
        RemoteInstallReason.DownloadFailed => "download-failed",
        RemoteInstallReason.VerifyFailed => "verify-failed",
        RemoteInstallReason.PlanRefused => "plan-refused",
        RemoteInstallReason.InstallFailed => "install-failed",
        RemoteInstallReason.NotInstalled => "not-installed",
        RemoteInstallReason.AlreadyInstalled => "already-installed",
        RemoteInstallReason.HasDependents => "has-dependents",
        RemoteInstallReason.Unavailable => "unavailable",
        RemoteInstallReason.HotLoadUnconfirmed => "hot-load-unconfirmed",
        RemoteInstallReason.Internal => "internal",
        _ => "none",
    };

    public static string StageName(RemoteInstallStage stage) => stage switch
    {
        RemoteInstallStage.Accepted => "accepted",
        RemoteInstallStage.Downloading => "downloading",
        RemoteInstallStage.Verifying => "verifying",
        RemoteInstallStage.Installing => "installing",
        RemoteInstallStage.HotLoaded => "hot-loaded",
        RemoteInstallStage.PendingRestart => "pending-restart",
        RemoteInstallStage.Removed => "removed",
        RemoteInstallStage.Refused => "refused",
        RemoteInstallStage.Failed => "failed",
        RemoteInstallStage.Cancelled => "cancelled",
        _ => "none",
    };

    public static string OpName(RemoteInstallOp op) => op switch
    {
        RemoteInstallOp.Install => "install",
        RemoteInstallOp.Update => "update",
        RemoteInstallOp.Uninstall => "uninstall",
        RemoteInstallOp.List => "list",
        RemoteInstallOp.Cancel => "cancel",
        _ => "none",
    };
}
