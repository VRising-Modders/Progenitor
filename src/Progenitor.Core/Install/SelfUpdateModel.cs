using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE5: the settings of Progenitor's own update. <c>[AutoUpdate] Progenitor</c> is Automatic on a client and
/// Notify on a server (chosen 2026-10-09, pending the user's confirmation). <c>[AutoUpdate] SelfUpdateDevOverride</c> is
/// a cfg-only developer key for the live test: it lets a development build or a process with the Dev bridge update
/// itself. Off by default; never set on a player's install.
/// </summary>
internal static class SelfUpdateSettings
{
    public const string Section = UpdateSettings.Section;
    public const string ProgenitorKey = "Progenitor";
    public const string DevOverrideKey = "SelfUpdateDevOverride";
    public const string ClientDefault = "Automatic";
    public const string ServerDefault = "Notify";

    public const string Help =
        "Automatic installs a new Progenitor release at the title screen (a new Bootstrap takes effect at the next start); "
        + "Notify only shows it; Off hides it. A Progenitor a mod manager installed, a development build and a release that "
        + "changes a protocol only notify. Progenitor's versions are dates, so Major versions does not apply to it.";

    public const string ServerHelp =
        "Automatic, Notify or Off. Automatic swaps a new Progenitor Core while no player is online; a new Bootstrap applies at "
        + "the next restart. A Progenitor a mod manager installed, a development build and a release that changes a protocol "
        + "only notify. Progenitor's versions are dates, so Major versions does not apply to it.";

    public const string DevOverrideHelp =
        "Developer testing only. Lets a development build, or a process with the Dev bridge loaded, update Progenitor itself "
        + "(normally they only report). Off by default.";

    public static string DefaultFor(bool server) => server ? ServerDefault : ClientDefault;

    /// <summary>This process's default (a server binds Notify even when the Preferences bindings are made there).</summary>
    public static string DefaultHere =>
#if PROGENITOR_TESTS
        ClientDefault;
#else
        DefaultFor(Plugin.IsServerProcess);
#endif
}

/// <summary>AUTOUPDATE5: one version of Progenitor's own package in the index.</summary>
internal sealed class SelfRelease
{
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
    public bool Active { get; set; } = true;
    public bool Deprecated { get; set; }

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package,
        ["version"] = Version,
        ["downloadUrl"] = DownloadUrl,
        ["size"] = Size,
        ["active"] = Active,
        ["deprecated"] = Deprecated,
    };
}

/// <summary>
/// AUTOUPDATE5: Progenitor's own versions from the Thunderstore v1 index body this process already read (the cached
/// <c>progenitor-cache</c> copy). No request of its own. Only the packages <see cref="LoaderPackages.IsProgenitorPackage"/>
/// names count.
/// </summary>
internal static class SelfIndex
{
    public static List<SelfRelease> Parse(string? json)
    {
        var list = new List<SelfRelease>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                string owner = SelfState.Str(p, "owner");
                string name = SelfState.Str(p, "name");
                string full = SelfState.Str(p, "full_name");
                if (!LoaderPackages.IsProgenitorPackage(owner, name, full)) continue;
                string key = full.Length > 0 ? full : owner + "-" + name;
                bool deprecated = p.TryGetProperty("is_deprecated", out var d) && d.ValueKind == JsonValueKind.True;
                if (!p.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in versions.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    string version = SelfState.Str(v, "version_number");
                    if (version.Length == 0) continue;
                    list.Add(new SelfRelease
                    {
                        Package = key,
                        Version = version,
                        DownloadUrl = SelfState.Str(v, "download_url"),
                        Size = v.TryGetProperty("file_size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) ? n : 0,
                        Active = !(v.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.False),
                        Deprecated = deprecated,
                    });
                }
            }
        }
        catch (JsonException) { return new List<SelfRelease>(); }

        return list;
    }
}

/// <summary>AUTOUPDATE5: how this Progenitor was installed, as far as updating itself is concerned.</summary>
internal enum SelfInstallKind
{
    /// <summary>Its files match the self-install record (or a ledger <c>framework</c> row): it may update itself.</summary>
    Managed,

    /// <summary>No record: installed by hand. Notify; the Install tab's <b>Update</b> installs a release and records it.</summary>
    Hand,

    /// <summary>A mod manager installed it. Notify only: update it where it was installed.</summary>
    ModManager,

    /// <summary>A record exists but the files changed since (another copy was put in place). Notify only.</summary>
    Changed,

    /// <summary>A development build (a sub-release version) or a process with the Dev bridge: reports only.</summary>
    Development,
}

/// <summary>AUTOUPDATE5: what detection reads (all game-free values).</summary>
internal sealed class SelfFacts
{
    public bool IsServer { get; set; }
    public string CoreVersion { get; set; } = "";
    public string BootstrapVersion { get; set; } = "";
    public string CoreSha { get; set; } = "";
    public string BootstrapSha { get; set; } = "";
    public SelfInstallRecord? Record { get; set; }

    /// <summary>A ledger <c>framework</c> row names these exact Bootstrap and Core files (a server folder this client installed).</summary>
    public bool LedgerFramework { get; set; }

    public bool ModManager { get; set; }
    public bool DevBridge { get; set; }
    public bool DevOverride { get; set; }
    public UpdateMode Mode { get; set; } = UpdateMode.Automatic;
    public SelfState State { get; set; } = new();

    /// <summary>
    /// Releases whose images were read and change a protocol (version → what changed): this session's, and the held notes
    /// of earlier sessions that still apply (<see cref="SelfHeldFile"/>). Filled by <see cref="SelfUpdates"/>; detection
    /// marks such a release notify only.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ProtocolChanges { get; set; }
}

/// <summary>AUTOUPDATE5: the detection result.</summary>
internal sealed class SelfOffer
{
    public static readonly SelfOffer None = new();

    public SelfInstallKind Kind { get; set; }

    /// <summary>The version running now, as updates compare it (the record's release, else the newer component version).</summary>
    public string Installed { get; set; } = "";

    /// <summary>The release offered, or null.</summary>
    public SelfRelease? Release { get; set; }

    /// <summary>
    /// The release changes a compatibility protocol (Bootstrap/Core or handshake), learned from its downloaded images: a
    /// breaking update, notify only (decided 2026-10-09). Empty when it does not, or before its images were read.
    /// Progenitor's versions are dates (<c>year.month.build</c>), so the version number never marks a release as breaking.
    /// </summary>
    public string ProtocolChange { get; set; } = "";

    public bool Breaking => ProtocolChange.Length > 0;

    /// <summary>The newest version is in the failed list (nothing newer exists): shown, never applied automatically.</summary>
    public string FailedBefore { get; set; } = "";

    /// <summary>Applied without asking in the next apply window.</summary>
    public bool Auto { get; set; }

    /// <summary>Why it is not automatic (empty when it is).</summary>
    public string NotAutoReason { get; set; } = "";

    /// <summary>A click may install it (managed or a hand install, not a development build, nothing holding it).</summary>
    public bool Clickable { get; set; }

    /// <summary>
    /// Why neither a click nor a pass may install it now although it is offered (C4: a build placed for a server,
    /// <see cref="SelfDetection.Hold"/>), or empty.
    /// </summary>
    public string Held { get; set; } = "";

    public bool Available => Release != null;

    /// <summary>The Install tab's line: <c>Progenitor 2026.10.12 → 2026.10.14</c>.</summary>
    public string Line => Release == null ? "" : "Progenitor " + Installed + " → " + Release.Version + (Breaking ? " · protocol change" : "");

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = Kind.ToString(),
        ["installed"] = Installed,
        ["release"] = Release?.Describe(),
        ["protocolChange"] = ProtocolChange,
        ["failedBefore"] = FailedBefore,
        ["auto"] = Auto,
        ["notAutoReason"] = NotAutoReason,
        ["clickable"] = Clickable,
        ["held"] = Held,
        ["line"] = Line,
    };
}

/// <summary>
/// AUTOUPDATE5: which Progenitor release is offered, and whether it applies itself (pure). The decisions of 2026-10-09:
/// a client applies automatically by default, a server notifies; a development build or a process with the Dev bridge
/// never updates itself, only reports; a copy a mod manager installed (no self-install record) only notifies. Progenitor's
/// version is a date (<c>year.month.build</c>): the year and month carry no meaning for updates (decided
/// 2026-10-09). A release is breaking only when it changes a protocol, which is known once its images were read
/// (<see cref="SelfFacts.ProtocolChanges"/>, <see cref="SelfProtocols"/>); such a release only notifies.
/// </summary>
internal static class SelfDetection
{
    public const string WhyNotify = "Update Progenitor is set to Notify";
    public const string WhyDev = "this is a development build of Progenitor; it never updates itself";
    public const string WhyBridge = "the Dev bridge is loaded; a test process never updates itself";
    public const string WhyModManager = "a mod manager installed Progenitor here; update it where you installed it";
    public const string WhyHand = "Progenitor was installed by hand; Update installs the release and records it";
    public const string WhyChanged = "Progenitor's files changed since Progenitor installed them";
    public const string WhyProtocol = "it changes a protocol; it is never applied without a click";

    /// <summary>A version with a sub-release number (<c>2026.10.12.3</c>): a development build (docs/versioning.md).</summary>
    public static bool IsSubRelease(string? version)
    {
        string v = (version ?? "").Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus);
        return v.Split('.').Length >= 4;
    }

    /// <summary>The release part of a version (<c>2026.10.12.3</c> → <c>2026.10.12</c>).</summary>
    public static string ReleaseOf(string? version)
    {
        string v = (version ?? "").Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus);
        var parts = v.Split('.');
        return parts.Length > 3 ? string.Join(".", parts, 0, 3) : v;
    }

    public static SelfInstallKind KindOf(SelfFacts f)
    {
        bool dev = IsSubRelease(f.CoreVersion) || IsSubRelease(f.BootstrapVersion) || f.DevBridge;
        if (dev && !f.DevOverride) return SelfInstallKind.Development;
        if (f.Record != null && f.Record.Matches(f.CoreSha, f.BootstrapSha)) return SelfInstallKind.Managed;
        if (RunsJournalRelease(f)) return SelfInstallKind.Managed;
        if (f.LedgerFramework) return SelfInstallKind.Managed;
        if (dev && f.DevOverride) return SelfInstallKind.Managed;
        if (f.ModManager) return SelfInstallKind.ModManager;
        if (f.Record != null) return SelfInstallKind.Changed;
        return SelfInstallKind.Hand;
    }

    /// <summary>The release of the update in progress runs (its trial, before the record is written): it is what is installed.</summary>
    public static bool RunsJournalRelease(SelfFacts f)
    {
        var j = f.State?.Journal;
        if (j == null || !j.InProgress || j.NewCore.Length == 0) return false;
        if (!string.Equals(j.NewCore, f.CoreSha, StringComparison.OrdinalIgnoreCase)) return false;
        return j.NewBootstrap.Length == 0 || string.Equals(j.NewBootstrap, f.BootstrapSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installed version updates compare with.</summary>
    public static string InstalledOf(SelfFacts f, SelfInstallKind kind)
    {
        if (kind == SelfInstallKind.Managed && f.Record != null && f.Record.Matches(f.CoreSha, f.BootstrapSha)) return f.Record.Version;
        if (RunsJournalRelease(f)) return f.State.Journal!.Version;
        string a = f.CoreVersion, b = f.BootstrapVersion;
        return UpdateVersions.Compare(a, b) >= 0 ? a : b;
    }

    /// <summary>
    /// The offer while something holds Progenitor's own update (<paramref name="hold"/>, C4: a build placed for a server):
    /// neither automatic nor clickable, with the hold as the reason, so the readback matches the refused click (live
    /// 2026-10-10: <c>clickable</c> read true while the click answered <c>held: …</c>). The offer itself is unchanged when
    /// nothing holds it.
    /// </summary>
    public static SelfOffer Hold(SelfOffer offer, string? hold)
    {
        if (offer == null || string.IsNullOrEmpty(hold) || !offer.Available) return offer!;
        return new SelfOffer
        {
            Kind = offer.Kind,
            Installed = offer.Installed,
            Release = offer.Release,
            ProtocolChange = offer.ProtocolChange,
            FailedBefore = offer.FailedBefore,
            Auto = false,
            NotAutoReason = hold!,
            Clickable = false,
            Held = hold!,
        };
    }

    public static SelfOffer Detect(SelfFacts facts, IReadOnlyList<SelfRelease> releases)
    {
        if (facts == null || facts.Mode == UpdateMode.Off) return SelfOffer.None;
        var kind = KindOf(facts);
        string installed = InstalledOf(facts, kind);
        SelfRelease? best = null;
        string failedNewest = "";
        foreach (var r in releases ?? Array.Empty<SelfRelease>())
        {
            if (r == null || !r.Active || r.Deprecated || r.Version.Length == 0) continue;
            if (!UpdateVersions.IsNewer(r.Version, installed)) continue;
            if (facts.State.IsFailed(r.Version))
            {
                if (failedNewest.Length == 0 || UpdateVersions.IsNewer(r.Version, failedNewest)) failedNewest = r.Version;
                continue;
            }

            if (best == null || UpdateVersions.IsNewer(r.Version, best.Version)) best = r;
        }

        var offer = new SelfOffer { Kind = kind, Installed = installed };
        if (best == null)
        {
            offer.FailedBefore = failedNewest;
            return offer;
        }

        offer.Release = best;
        if (facts.ProtocolChanges != null && facts.ProtocolChanges.TryGetValue(best.Version, out var change)) offer.ProtocolChange = change ?? "";
        offer.Clickable = kind is SelfInstallKind.Managed or SelfInstallKind.Hand;
        if (failedNewest.Length > 0 && UpdateVersions.IsNewer(failedNewest, best.Version)) offer.FailedBefore = failedNewest;
        string why = kind switch
        {
            SelfInstallKind.Development => facts.DevBridge && !IsSubRelease(facts.CoreVersion) && !IsSubRelease(facts.BootstrapVersion) ? WhyBridge : WhyDev,
            SelfInstallKind.ModManager => WhyModManager,
            SelfInstallKind.Hand => WhyHand,
            SelfInstallKind.Changed => WhyChanged,
            _ => "",
        };
        if (why.Length == 0 && offer.Breaking) why = WhyProtocol + " (" + offer.ProtocolChange + ")";
        if (why.Length == 0 && facts.Mode != UpdateMode.Automatic) why = WhyNotify;
        offer.NotAutoReason = why;
        offer.Auto = why.Length == 0;
        return offer;
    }
}

/// <summary>
/// The compatibility protocols a Progenitor release is judged by (decided 2026-10-09): the Bootstrap/Core protocol
/// (<c>CoreLayout.ProtocolVersion</c> / <c>BootstrapLink.ProtocolVersion</c>) and the handshake protocol
/// (<c>Networking.Handshake.ProtocolVersion</c>). A release that changes either is breaking; one that keeps both is a
/// normal update whatever its date version says (pure).
/// </summary>
internal static class SelfProtocols
{
    /// <summary>
    /// What <paramref name="image"/> changes against the running protocols, or empty when nothing does. An image without
    /// a readable handshake protocol (0) cannot be shown to keep it, so it counts as a change.
    /// </summary>
    public static string ChangeOf(SelfImage image, int runningBootstrapProtocol, int runningHandshakeProtocol)
    {
        if (image == null) return "";
        var parts = new List<string>();
        if (image.BootstrapProtocol != runningBootstrapProtocol || image.CoreProtocol != runningBootstrapProtocol)
            parts.Add("Bootstrap protocol " + runningBootstrapProtocol + " → " + image.BootstrapProtocol);
        if (image.HandshakeProtocol != runningHandshakeProtocol)
            parts.Add("handshake protocol " + runningHandshakeProtocol + " → "
                + (image.HandshakeProtocol > 0 ? image.HandshakeProtocol.ToString(CultureInfo.InvariantCulture) : "unknown"));
        return string.Join(", ", parts);
    }
}

/// <summary>AUTOUPDATE5: a downloaded Progenitor release, read from its package zip (nothing loaded or run).</summary>
internal sealed class SelfImage
{
    public byte[] Core { get; set; } = Array.Empty<byte>();
    public byte[] Bootstrap { get; set; } = Array.Empty<byte>();

    /// <summary>The package's preloader (<c>Progenitor.Preloader.patcher</c>), or empty when it ships none.</summary>
    public byte[] Preloader { get; set; } = Array.Empty<byte>();

    public Dictionary<string, byte[]> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string CoreSha { get; set; } = "";
    public string BootstrapSha { get; set; } = "";

    /// <summary>The sha256 of the downloaded package zip (set by the downloader's caller; empty when unknown).</summary>
    public string PackageSha { get; set; } = "";

    public int CoreProtocol { get; set; }
    public int BootstrapProtocol { get; set; }

    /// <summary>The Core image's <c>Progenitor.Networking.Handshake.ProtocolVersion</c>, or 0 when it has none.</summary>
    public int HandshakeProtocol { get; set; }

    public string CoreVersion { get; set; } = "";
    public string BootstrapVersion { get; set; } = "";

    /// <summary>The package's own version (<c>manifest.json</c>), or empty.</summary>
    public string PackageVersion { get; set; } = "";

    public string Changelog { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// AUTOUPDATE5: reads a Progenitor package zip: <c>BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll</c>,
/// <c>BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core</c>, its <c>addresslib/*.json</c> and the shipped
/// preloader. A three-location package (<c>BepInEx/plugins/Progenitor.Bootstrap.dll</c>,
/// <c>BepInEx/progenitor-core/Progenitor.dll</c>) still reads, so a kept or older release can be installed back. The Core/Bootstrap protocol and the
/// versions are read from each image's metadata (<c>Progenitor.BootstrapLink.ProtocolVersion</c>,
/// <c>Progenitor.Bootstrap.CoreLayout.ProtocolVersion</c>), and Core's handshake protocol
/// (<c>Progenitor.Networking.Handshake.ProtocolVersion</c>); nothing is loaded.
/// </summary>
internal static class SelfImageReader
{
    public const long MaxEntryBytes = 64L * 1024 * 1024;
    public const string CoreProtocolType = "Progenitor.BootstrapLink";
    public const string BootstrapProtocolType = "Progenitor.Bootstrap.CoreLayout";
    public const string HandshakeProtocolType = "Progenitor.Networking.Handshake";
    public const string ProtocolField = "ProtocolVersion";

    public static SelfImage Read(byte[] zip)
    {
        var image = new SelfImage();
        bool coreFromImageName = false;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string path = entry.FullName.Replace('\\', '/').TrimStart('/');
                string lower = path.ToLowerInvariant();
                if (entry.Length > MaxEntryBytes) { image.Error = path + " is too large"; return image; }
                if (lower == "manifest.json")
                    image.PackageVersion = SelfState.Str(JsonDocument.Parse(ReadAll(entry)).RootElement, "version_number");
                else if (lower == "changelog.md")
                    image.Changelog = System.Text.Encoding.UTF8.GetString(ReadAll(entry));
                else if (IsBootstrapEntry(lower))
                    image.Bootstrap = ReadAll(entry);
                else if (IsCoreEntry(lower, InstallLayout.CoreFileName))
                {
                    image.Core = ReadAll(entry);
                    coreFromImageName = true;
                }
                else if (IsCoreEntry(lower, InstallLayout.LegacyCoreFileName))
                {
                    // A three-location package; the single-folder image wins when a package carries both.
                    if (!coreFromImageName) image.Core = ReadAll(entry);
                }
                else if (lower.EndsWith("/" + InstallLayout.ShippedPreloaderFileName.ToLowerInvariant(), StringComparison.Ordinal)
                         || lower.EndsWith("/patchers/" + InstallLayout.PreloaderFileName.ToLowerInvariant(), StringComparison.Ordinal))
                    image.Preloader = ReadAll(entry);
                else if (lower.Contains("/progenitor-core/addresslib/") && lower.EndsWith(".json", StringComparison.Ordinal)
                         && (Path.GetFileName(lower).StartsWith("client-", StringComparison.Ordinal) || Path.GetFileName(lower).StartsWith("server-", StringComparison.Ordinal)))
                    image.Tables[Path.GetFileName(path)] = ReadAll(entry);
            }
        }
        catch (Exception e)
        {
            image.Error = "the package could not be read: " + e.Message;
            return image;
        }

        if (image.Core.Length == 0) { image.Error = "the package has no BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core"; return image; }
        if (image.Bootstrap.Length == 0) { image.Error = "the package has no BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll"; return image; }
        image.CoreSha = SelfFiles.Sha256(image.Core);
        image.BootstrapSha = SelfFiles.Sha256(image.Bootstrap);
        if (!TryReadAssembly(image.Core, "Progenitor", CoreProtocolType, out int cp, out string cv, out string ce)) { image.Error = "Core: " + ce; return image; }
        if (!TryReadAssembly(image.Bootstrap, "Progenitor.Bootstrap", BootstrapProtocolType, out int bp, out string bv, out string be)) { image.Error = "Bootstrap: " + be; return image; }
        image.CoreProtocol = cp;
        image.CoreVersion = cv;
        image.BootstrapProtocol = bp;
        image.BootstrapVersion = bv;
        image.HandshakeProtocol = TryReadConstant(image.Core, HandshakeProtocolType, out int hp) ? hp : 0;
        return image;
    }

    /// <summary>
    /// <c>Progenitor.Bootstrap.dll</c> anywhere under <c>plugins/</c>: the single-folder
    /// <c>plugins/Progenitor/Progenitor.Bootstrap.dll</c> or the three-location <c>plugins/Progenitor.Bootstrap.dll</c>.
    /// </summary>
    internal static bool IsBootstrapEntry(string lower)
    {
        string leaf = "/" + InstallLayout.BootstrapFileName.ToLowerInvariant();
        if (!(lower.EndsWith(leaf, StringComparison.Ordinal) || lower == leaf.Substring(1))) return false;
        return lower.StartsWith("plugins/", StringComparison.Ordinal) || lower.Contains("/plugins/");
    }

    /// <summary>A Core image named <paramref name="fileName"/> directly in a <c>progenitor-core</c> folder.</summary>
    internal static bool IsCoreEntry(string lower, string fileName)
    {
        string tail = InstallLayout.CoreFolderName + "/" + fileName.ToLowerInvariant();
        return lower == tail || lower.EndsWith("/" + tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mod compatibility C4: a kept release (<see cref="SelfStore"/>) read back as an image: its <c>Progenitor.dll</c>, its
    /// Bootstrap when one was kept, and its address tables. Nothing is loaded; the protocols are read from metadata.
    /// </summary>
    public static SelfImage ReadKept(string dir)
    {
        var image = new SelfImage();
        try
        {
            string core = Path.Combine(dir, SelfPaths.CoreFile);
            if (!File.Exists(core)) { image.Error = "the kept copy has no " + SelfPaths.CoreFile; return image; }
            image.Core = File.ReadAllBytes(core);
            string boot = Path.Combine(dir, SelfPaths.BootstrapFile);
            if (File.Exists(boot)) image.Bootstrap = File.ReadAllBytes(boot);
            string tables = Path.Combine(dir, SelfPaths.TablesFolder);
            if (Directory.Exists(tables))
                foreach (string f in Directory.GetFiles(tables, "*.json"))
                    image.Tables[Path.GetFileName(f)] = File.ReadAllBytes(f);
        }
        catch (Exception e)
        {
            image.Error = "the kept copy could not be read: " + e.Message;
            return image;
        }

        image.CoreSha = SelfFiles.Sha256(image.Core);
        if (!TryReadAssembly(image.Core, "Progenitor", CoreProtocolType, out int cp, out string cv, out string ce)) { image.Error = "Core: " + ce; return image; }
        image.CoreProtocol = cp;
        image.CoreVersion = cv;
        image.HandshakeProtocol = TryReadConstant(image.Core, HandshakeProtocolType, out int hp) ? hp : 0;
        if (image.Bootstrap.Length > 0)
        {
            image.BootstrapSha = SelfFiles.Sha256(image.Bootstrap);
            if (TryReadAssembly(image.Bootstrap, "Progenitor.Bootstrap", BootstrapProtocolType, out int bp, out string bv, out _))
            {
                image.BootstrapProtocol = bp;
                image.BootstrapVersion = bv;
            }
            else
            {
                // An unreadable kept Bootstrap is left out: only the hot route (Core alone) can use this copy.
                image.Bootstrap = Array.Empty<byte>();
                image.BootstrapSha = "";
            }
        }

        return image;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The assembly's simple name must be <paramref name="name"/>; reads the protocol constant and the version.</summary>
    internal static bool TryReadAssembly(byte[] bytes, string name, string protocolType, out int protocol, out string version, out string error)
    {
        protocol = 0;
        version = "";
        error = "";
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata) { error = "not a managed image"; return false; }
            var md = pe.GetMetadataReader();
            var asm = md.GetAssemblyDefinition();
            string simple = md.GetString(asm.Name);
            if (!string.Equals(simple, name, StringComparison.Ordinal)) { error = "the image is " + simple + ", not " + name; return false; }
            version = FormatVersion(asm.Version);
            if (FindProtocol(md, protocolType, out protocol)) return true;
            error = "no " + protocolType + "." + ProtocolField;
            return false;
        }
        catch (Exception e)
        {
            error = "the image could not be read: " + e.Message;
            return false;
        }
    }

    /// <summary>Reads <c>&lt;protocolType&gt;.ProtocolVersion</c> (an int constant) from an image; false when it has none.</summary>
    internal static bool TryReadConstant(byte[] bytes, string protocolType, out int protocol)
    {
        protocol = 0;
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            return pe.HasMetadata && FindProtocol(pe.GetMetadataReader(), protocolType, out protocol);
        }
        catch { return false; }
    }

    private static bool FindProtocol(MetadataReader md, string protocolType, out int protocol)
    {
        protocol = 0;
        int dot = protocolType.LastIndexOf('.');
        string ns = protocolType.Substring(0, dot), type = protocolType.Substring(dot + 1);
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (md.GetString(td.Name) != type || md.GetString(td.Namespace) != ns) continue;
            foreach (var fh in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                if (md.GetString(fd.Name) != ProtocolField) continue;
                var ch = fd.GetDefaultValue();
                if (ch.IsNil) return false;
                var c = md.GetConstant(ch);
                if (c.TypeCode != ConstantTypeCode.Int32) return false;
                protocol = md.GetBlobReader(c.Value).ReadInt32();
                return true;
            }
        }

        return false;
    }

    /// <summary><c>2026.10.12.0</c> → <c>2026.10.12</c>; a sub-release keeps its fourth part.</summary>
    internal static string FormatVersion(Version v) =>
        v.Revision > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", v.Major, v.Minor, v.Build, v.Revision)
            : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", v.Major, v.Minor, Math.Max(0, v.Build));
}

/// <summary>AUTOUPDATE5: how a release is applied.</summary>
internal enum SelfRouteKind
{
    /// <summary>Same bytes as the running copy: nothing to apply (the record is brought up to date).</summary>
    Current,

    /// <summary>Only Core changed and its protocol matches the running Bootstrap: Core is swapped in place.</summary>
    Hot,

    /// <summary>Bootstrap changed: staged as <c>.next</c>, the release starts at the next launch.</summary>
    Restart,

    Refused,
}

/// <summary>AUTOUPDATE5: the route of a release against the running copy (pure).</summary>
internal static class SelfRoute
{
    public static (SelfRouteKind Kind, string Why) Decide(SelfImage image, string runningCoreSha, string runningBootstrapSha, int runningProtocol)
    {
        if (image == null || !image.Ok) return (SelfRouteKind.Refused, image?.Error ?? "no image");
        if (image.CoreProtocol <= 0 || image.CoreProtocol != image.BootstrapProtocol)
            return (SelfRouteKind.Refused, "the release's Core (protocol " + image.CoreProtocol + ") and Bootstrap (protocol " + image.BootstrapProtocol + ") do not match");
        bool sameBootstrap = Same(image.BootstrapSha, runningBootstrapSha);
        bool sameCore = Same(image.CoreSha, runningCoreSha);
        if (sameBootstrap && sameCore) return (SelfRouteKind.Current, "the running copy is this release");
        if (sameBootstrap)
        {
            if (image.CoreProtocol != runningProtocol)
                return (SelfRouteKind.Refused, "its Core needs protocol " + image.CoreProtocol + " but its own Bootstrap is the running one (protocol " + runningProtocol + ")");
            return (SelfRouteKind.Hot, "Core only, protocol " + runningProtocol + ": swapped in place");
        }

        return (SelfRouteKind.Restart, image.CoreProtocol == runningProtocol
            ? "a new Bootstrap: it starts at the next launch"
            : "a new Bootstrap and Core protocol " + image.CoreProtocol + " (running " + runningProtocol + "): both start at the next launch");
    }

    /// <summary>
    /// Mod compatibility C4: the route of a build placed for a server (or the player's own copy put back after it). Only Core
    /// is compared with the server, so a Core whose protocol the running Bootstrap accepts is swapped in place and the running
    /// Bootstrap stays, whatever the release's own Bootstrap is. Another protocol needs the image's own Bootstrap: staged, it
    /// starts at the next launch (the restart route).
    /// </summary>
    public static (SelfRouteKind Kind, string Why) Placement(SelfImage image, string runningCoreSha, int runningProtocol)
    {
        if (image == null || !image.Ok) return (SelfRouteKind.Refused, image?.Error ?? "no image");
        if (image.CoreProtocol <= 0) return (SelfRouteKind.Refused, "its Core has no readable protocol");
        if (Same(image.CoreSha, runningCoreSha)) return (SelfRouteKind.Current, "the running Core is this build");
        if (image.CoreProtocol == runningProtocol) return (SelfRouteKind.Hot, "Core only, protocol " + runningProtocol + ": swapped in place, the running Bootstrap stays");
        if (image.Bootstrap.Length == 0 || image.BootstrapProtocol != image.CoreProtocol)
            return (SelfRouteKind.Refused, "its Core needs Bootstrap protocol " + image.CoreProtocol + " (this Bootstrap is " + runningProtocol + ") and no matching Bootstrap came with it");
        return (SelfRouteKind.Restart, "its Core needs Bootstrap protocol " + image.CoreProtocol + " (this one is " + runningProtocol + "): both start at the next launch");
    }

    private static bool Same(string a, string b) => (a ?? "").Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// AUTOUPDATE5 (mod-compatibility decision 7): the version store for Progenitor itself,
/// <c>progenitor-versions/progenitor/&lt;version&gt;-&lt;sha8&gt;/</c>. Every update keeps the release it installs and the copy
/// that ran before it, so an Exact server's Core can be found without a download (selection only: the swap for a server
/// is M7). Bounded at <see cref="MaxKept"/> releases; the journal's two are never pruned.
/// </summary>
internal static class SelfStore
{
    public const int MaxKept = 4;

    /// <summary>Keep a release (idempotent by folder). Returns the folder relative to the BepInEx root, or empty with an error.</summary>
    public static string Keep(string root, SelfKept info, byte[] core, byte[] bootstrap, IReadOnlyDictionary<string, byte[]>? tables, out string error)
    {
        error = "";
        try
        {
            string dirName = SelfPaths.KeptDirName(info.Version, info.CoreSha);
            string dir = Path.Combine(SelfPaths.Store(root), dirName);
            Directory.CreateDirectory(dir);
            if (!SelfFiles.WriteAtomic(Path.Combine(dir, SelfPaths.CoreFile), core, out error)) return "";
            if (bootstrap.Length > 0 && !SelfFiles.WriteAtomic(Path.Combine(dir, SelfPaths.BootstrapFile), bootstrap, out error)) return "";
            if (tables != null)
                foreach (var kv in tables)
                {
                    string leaf = Path.GetFileName(kv.Key);
                    if (leaf.Length == 0) continue;
                    if (!SelfFiles.WriteAtomic(Path.Combine(dir, SelfPaths.TablesFolder, leaf), kv.Value, out error)) return "";
                }

            info.KeptUtc = info.KeptUtc == default ? DateTime.UtcNow : info.KeptUtc;
            if (!SelfFiles.WriteAtomic(Path.Combine(dir, SelfPaths.KeptInfoName), info.ToJson(), out error)) return "";
            return SelfPaths.Relative(root, dir);
        }
        catch (Exception e)
        {
            error = e.Message;
            return "";
        }
    }

    /// <summary>Keep the address tables of a live folder (the running copy's) with a kept release.</summary>
    public static Dictionary<string, byte[]> LiveTables(string root)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = SelfPaths.Tables(root);
            if (!Directory.Exists(dir)) return map;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                string leaf = Path.GetFileName(f);
                if (leaf.StartsWith("client-", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("server-", StringComparison.OrdinalIgnoreCase))
                    map[leaf] = File.ReadAllBytes(f);
            }
        }
        catch { /* the tables are optional */ }

        return map;
    }

    public static List<SelfKept> List(string root)
    {
        var list = new List<SelfKept>();
        try
        {
            string store = SelfPaths.Store(root);
            if (!Directory.Exists(store)) return list;
            foreach (string dir in Directory.GetDirectories(store))
            {
                var k = SelfFiles.ReadKept(dir);
                if (k != null && File.Exists(Path.Combine(dir, SelfPaths.CoreFile))) list.Add(k);
            }
        }
        catch { /* an unreadable store lists nothing */ }

        list.Sort((a, b) => b.KeptUtc.CompareTo(a.KeptUtc));
        return list;
    }

    /// <summary>
    /// The kept release an Exact server's Core matches: its sha256 when given (a release has one sha), else the newest kept
    /// copy of that version. Null when none is kept (the server's release is then downloaded, M7).
    /// </summary>
    public static SelfKept? Select(IReadOnlyList<SelfKept> kept, string version, string? coreSha)
    {
        SelfKept? best = null;
        foreach (var k in kept ?? Array.Empty<SelfKept>())
        {
            if (!string.IsNullOrWhiteSpace(coreSha))
            {
                if (string.Equals(k.CoreSha, coreSha!.Trim(), StringComparison.OrdinalIgnoreCase)) return k;
                continue;
            }

            if (UpdateVersions.Compare(SelfDetection.ReleaseOf(k.Version), SelfDetection.ReleaseOf(version)) != 0) continue;
            if (best == null || k.KeptUtc > best.KeptUtc) best = k;
        }

        return best;
    }

    /// <summary>The folders to delete so at most <see cref="MaxKept"/> stay; <paramref name="protect"/> are never chosen.</summary>
    public static List<string> PrunePlan(IReadOnlyList<SelfKept> kept, ICollection<string> protect)
    {
        var sorted = new List<SelfKept>(kept ?? Array.Empty<SelfKept>());
        sorted.Sort((a, b) => b.KeptUtc.CompareTo(a.KeptUtc));
        var drop = new List<string>();
        int keep = 0;
        foreach (var k in sorted)
        {
            bool isProtected = false;
            foreach (var p in protect)
                if (string.Equals(Path.GetFileName((p ?? "").TrimEnd('/', '\\')), k.Dir, StringComparison.OrdinalIgnoreCase)) isProtected = true;
            if (isProtected || keep < MaxKept) { keep++; continue; }
            drop.Add(k.Dir);
        }

        return drop;
    }

    public static int Prune(string root, ICollection<string> protect)
    {
        int n = 0;
        foreach (string dir in PrunePlan(List(root), protect))
        {
            try
            {
                Directory.Delete(Path.Combine(SelfPaths.Store(root), dir), recursive: true);
                n++;
            }
            catch { /* tried again after the next update */ }
        }

        return n;
    }
}
