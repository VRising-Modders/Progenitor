using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>Where a required row's requirement comes from (the same two origins as the deny report).</summary>
internal enum JoinRowOrigin
{
    /// <summary>This server asked for the mod (its handshake request).</summary>
    ServerRequires = 1,

    /// <summary>This client declared the mod <c>ServerMustHave</c> and the server does not run it.</summary>
    YouRequire = 2,

    /// <summary>
    /// CLIENTPOLICY2: the admins require this client mod (the signed section's <c>requiredClient</c>); the server need not
    /// run it. Judged with the admins' version rule (<see cref="ClientModRules"/>) against this client's loaded mods.
    /// </summary>
    ClientPolicy = 3,

    /// <summary>
    /// Mod compatibility C4: the server holds players to its own Progenitor build (Exact version for all; the signed catalog's
    /// <c>progenitor</c> and <c>progenitorSha</c>). One row at most, id <see cref="ProgenitorExact.RowId"/>.
    /// </summary>
    Progenitor = 4,
}

/// <summary>A required row's state against the version the server needs.</summary>
internal enum JoinRowStatus
{
    /// <summary>The server's own join check accepts this client's copy.</summary>
    Ready = 0,

    /// <summary>This client has no copy.</summary>
    Missing = 1,

    /// <summary>This client has an older (or otherwise different) version.</summary>
    Older = 2,

    /// <summary>This client has a newer version than the server runs ("you're ahead").</summary>
    Newer = 3,

    /// <summary>A mod this client requires that the server does not run.</summary>
    ServerLacks = 4,

    /// <summary>
    /// JOINTURNON: this client's own copy is installed but the player turned it off in the Mods list, and that copy
    /// satisfies the server's version rule (<see cref="JoinOffCopy"/>). A per-server pause is not this.
    /// </summary>
    TurnedOff = 5,
}

/// <summary>The one action a required row offers.</summary>
internal enum JoinRowAction
{
    /// <summary>Nothing to do: a green check and <c>Ready</c>.</summary>
    Ready = 0,

    /// <summary>Install from Thunderstore (stage 2: answered with "coming in a later update").</summary>
    Install = 1,

    /// <summary>Update from Thunderstore (stage 2, as Install).</summary>
    Update = 2,

    // 3 was UseServerCopy (download the server's own copy). Removed 2026-10-05: servers no longer send mod files.

    /// <summary>No source anywhere: copy a short message for the admins.</summary>
    AskAdmin = 4,

    /// <summary>This client is ahead and the server has no copy: copy a short message for the admins.</summary>
    TellAdmin = 5,

    /// <summary>A mod this client requires and the server does not run: turn it off to join.</summary>
    TurnOff = 6,

    /// <summary>
    /// JOINFLOW2: this client is ahead, and Thunderstore still serves the server's
    /// exact older version: install that version; the newer copy is kept for other servers (JOINFLOW4).
    /// </summary>
    UseVersion = 7,

    /// <summary>
    /// JOINFLOW4: a kept (parked) copy of the version this server needs is on disk: swap it in, no download (now when
    /// the reloader can, else at the next start).
    /// </summary>
    UseKept = 8,

    /// <summary>
    /// JOINTURNON: the player's own copy is installed but turned off in the Mods list and satisfies the server's
    /// version rule: turn it on through the Mods list's switch (now when it hot-loads, else at the next start). No download.
    /// </summary>
    TurnOn = 9,

    /// <summary>
    /// Mod compatibility C4: put the server's Progenitor build in place for this server (a kept copy, else its Thunderstore
    /// release) at the title screen; the player's own build comes back after leaving.
    /// </summary>
    UseProgenitor = 10,
}

/// <summary>
/// JOINTURNON: a copy of a mod this client has installed that the player turned off in the Mods list — a Progenitor
/// mod held off by <c>ModEnableState</c>, or an ordinary BepInEx plugin whose DLLs the preloader renamed off. The
/// Mods list's switch is the single source of truth for it (<c>ModSwitch</c>). A per-server pause
/// (<c>ServerModPause</c>) is <b>not</b> an off copy: the server, not the player, holds that one.
/// </summary>
internal sealed class JoinOffCopy
{
    /// <summary>The off copy's version (its plugin metadata), or empty when it cannot be read.</summary>
    public string Version { get; init; } = "";

    /// <summary>The switch is live (a hot-loadable Progenitor mod): Turn on loads it now. Else it changes at the next start.</summary>
    public bool AppliesNow { get; init; }

    /// <summary>The switch is already on and the copy loads at the next start (turned on earlier, waiting for the restart).</summary>
    public bool TurnsOnAtNextStart { get; init; }

    /// <summary>
    /// A Progenitor mod (held off by <c>ModEnableState</c>): an install, update or server copy of it switches it on first,
    /// so the new copy is not held off. An ordinary plugin's renamed-off files are left to its own switch.
    /// </summary>
    public bool TurnsOnWithInstall { get; init; }

    /// <summary>
    /// Live 2026-10-05: switched on, but held by the per-server pause (the joined server does not run it). Not the
    /// player's choice: a required row ignores it (the pause keeps its own behavior); a recommended row reads installed
    /// (<c>Paused here</c>), so its switch never downloads a copy that is on disk.
    /// </summary>
    public bool PausedHere { get; init; }

    /// <summary>The route as the readback names it: <c>now</c> or <c>restart</c>.</summary>
    public string Route => AppliesNow && !TurnsOnAtNextStart ? "now" : "restart";
}

/// <summary>JOINTURNON: one mod the join screens turn on (a required row's Turn on, or a checked recommended row).</summary>
internal sealed class JoinTurnOnTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The off copy's version (what will run once it is on).</summary>
    public string Version { get; init; } = "";

    public bool Required { get; init; }

    public bool AppliesNow { get; init; }

    /// <summary>Already switched on; it waits for the next start.</summary>
    public bool TurnsOnAtNextStart { get; init; }

    internal static JoinTurnOnTarget From(JoinRequiredRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Version = row.OffCopy?.Version ?? "",
        Required = true,
        AppliesNow = row.OffCopy?.AppliesNow ?? false,
        TurnsOnAtNextStart = row.OffCopy?.TurnsOnAtNextStart ?? false,
    };

    internal static JoinTurnOnTarget From(JoinRecommendedRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Version = row.OffCopy?.Version ?? "",
        Required = false,
        AppliesNow = row.OffCopy?.AppliesNow ?? false,
        TurnsOnAtNextStart = row.OffCopy?.TurnsOnAtNextStart ?? false,
    };
}

/// <summary>Short tags after a row's version line.</summary>
[Flags]
internal enum JoinRowTags
{
    None = 0,
    Ahead = 1,
    AppliesNow = 2,
    NeedsRestart = 4,
}

/// <summary>Where a row can be fetched from.</summary>
[Flags]
internal enum JoinRowSource
{
    None = 0,
    Thunderstore = 1,
    // 2 was Server (the server's own signed copy). Removed 2026-10-05: mods come only from Thunderstore.

    /// <summary>
    /// LOCALJOIN1 (debug): the configured local test index (<c>[Install] LocalIndexPath</c>) provides this mod, which
    /// the server named no usable source for. Never set while that key is empty, never over a source the server named.
    /// </summary>
    LocalTest = 4,
}

/// <summary>One row of "Required to join".</summary>
internal sealed class JoinRequiredRow
{
    internal JoinRequiredRow(
        string id, string name, JoinRowOrigin origin, string required, string? have, JoinRowStatus status,
        JoinRowSource source, string? package, long size, string? sha256, bool hotLoadable, JoinRowAction action,
        JoinRowTags tags)
    {
        Id = id;
        Name = name;
        Origin = origin;
        RequiredVersion = required;
        Have = have;
        Status = status;
        Source = source;
        Package = package;
        Size = size;
        Sha256 = sha256;
        HotLoadable = hotLoadable;
        Action = action;
        Tags = tags;
    }

    public string Id { get; }
    public string Name { get; }
    public JoinRowOrigin Origin { get; }
    public string RequiredVersion { get; }
    public string? Have { get; }
    public JoinRowStatus Status { get; }
    public JoinRowSource Source { get; }

    /// <summary>Thunderstore <c>Owner-Name</c> from the signed catalog, or null.</summary>
    public string? Package { get; }

    /// <summary>Size of the server's loaded copy as the signed catalog states it, or 0. Informational only.</summary>
    public long Size { get; }

    /// <summary>sha256 of the server's loaded copy as the signed catalog states it, or null. Informational only.</summary>
    public string? Sha256 { get; }

    public bool HotLoadable { get; }
    public JoinRowAction Action { get; }
    public JoinRowTags Tags { get; }

    public bool Ready => Status == JoinRowStatus.Ready;

    /// <summary>LOCALJOIN1: the package is the local test index's, not one the server named.</summary>
    public bool LocalTest => (Source & JoinRowSource.LocalTest) != 0;

    /// <summary>LOCALJOIN1: why the local test source is not offered for this row (it lacks the version), or empty.</summary>
    public string LocalTestNote { get; internal set; } = "";

    /// <summary>JOINFLOW2: whether Thunderstore serves the exact version (<see cref="Install.PackageVersionAvailability"/>).</summary>
    public Install.PackageVersionAvailability Availability { get; internal set; }

    /// <summary>JOINFLOW2: the server's version rule for this mod (the handshake's), which the Thunderstore lookup honors.</summary>
    public NetworkVersionRule VersionRule { get; internal set; } = NetworkVersionRule.Exact;

    /// <summary>JOINFLOW2: the newest version Thunderstore serves of <see cref="Package"/>, or empty when unknown.</summary>
    public string ThunderstoreLatest { get; internal set; } = "";

    /// <summary>JOINFLOW2: a Thunderstore row whose exact version is no longer served (or that is not listed).</summary>
    public bool VersionGone => Package != null
        && Availability is Install.PackageVersionAvailability.VersionGone or Install.PackageVersionAvailability.NotListed;

    /// <summary>JOINFLOW2: the same row with other tags (what an install's outcome proved replaces the prediction).</summary>
    internal JoinRequiredRow WithTags(JoinRowTags tags) =>
        tags == Tags ? this : new JoinRequiredRow(Id, Name, Origin, RequiredVersion, Have, Status, Source, Package, Size, Sha256, HotLoadable, Action, tags)
        {
            Availability = Availability,
            ThunderstoreLatest = ThunderstoreLatest,
            VersionRule = VersionRule,
            Kept = Kept,
            OffCopy = OffCopy,
            Reason = Reason,
            PolicyRule = PolicyRule,
            MatchRule = MatchRule,
            ModId = ModId,
            LocalTestNote = LocalTestNote,
            ProgenitorNote = ProgenitorNote,
            ProgenitorSameVersion = ProgenitorSameVersion,
        };

    /// <summary>CLIENTPOLICY2: the admins' reason for a required client mod (drawn in quotes), else empty.</summary>
    public string Reason { get; internal set; } = "";

    /// <summary>CLIENTPOLICY2: the admins' version rule of a required client mod (<see cref="JoinRowOrigin.ClientPolicy"/>), else null.</summary>
    public ClientModRule? PolicyRule { get; internal set; }

    /// <summary>
    /// Mod compatibility C3/C4 wording: how the server judges a mod it runs when not by ranges (an admins' per-mod rule, else
    /// Exact or Same minor for all, from its catalog), so the row says <c>Needs exactly 1.2.0</c> or <c>Needs 1.2.x</c>.
    /// Null under declared ranges and from a server whose catalog does not say. Exact for the Progenitor row.
    /// </summary>
    public ClientModRule? MatchRule { get; internal set; }

    /// <summary>
    /// C4, the Progenitor row: why it cannot be fixed here (Tell the admin), or where Use Progenitor takes the build from
    /// (<c>kept, no download</c>, <c>from Thunderstore</c>); empty when ready.
    /// </summary>
    public string ProgenitorNote { get; internal set; } = "";

    /// <summary>C4, the Progenitor row: this client runs another build of the server's release number.</summary>
    public bool ProgenitorSameVersion { get; internal set; }

    private string? _modId;

    /// <summary>
    /// The plugin GUID an install must hold (<c>JoinInstallRequest.ModId</c>): the row's id, except a required client mod
    /// the admins named by its Thunderstore package alone (empty: the install does not check the plugin).
    /// </summary>
    public string ModId
    {
        get => _modId ?? Id;
        internal set => _modId = value;
    }

    /// <summary>JOINFLOW2: the row installs from Thunderstore (Install, Update or Use version).</summary>
    public bool Thunderstore => Action is JoinRowAction.Install or JoinRowAction.Update or JoinRowAction.UseVersion;

    /// <summary>JOINFLOW4: this mod's kept versions as the store sees them, or null when none are known.</summary>
    public JoinKeptInfo? Kept { get; internal set; }

    /// <summary>
    /// JOINTURNON: the player's own copy of this mod, installed but turned off in the Mods list, or null. Set whatever its
    /// version: a copy at the wrong version keeps the row's install path, and that fix turns the mod on.
    /// </summary>
    public JoinOffCopy? OffCopy { get; internal set; }

    /// <summary>JOINTURNON: a Turn on that was pressed (or a switch already on) and waits for the next start.</summary>
    public bool TurnOnPending => Action == JoinRowAction.TurnOn && OffCopy is { TurnsOnAtNextStart: true };
}

/// <summary>One row of "Recommended by this server".</summary>
internal sealed class JoinRecommendedRow
{
    internal JoinRecommendedRow(RecommendedMod mod, string name, string? installedVersion, JoinOffCopy? offCopy = null)
    {
        Mod = mod;
        Name = name;
        OffCopy = offCopy;
        InstalledVersion = installedVersion ?? (offCopy != null ? offCopy.Version : null);
    }

    public RecommendedMod Mod { get; }
    public string Id => Mod.Id;
    public string Name { get; }

    /// <summary>The loaded version when this client has the mod (or the version of its turned-off copy), else null.</summary>
    public string? InstalledVersion { get; }

    /// <summary>JOINTURNON: the player's own copy, installed but turned off in the Mods list, or null.</summary>
    public JoinOffCopy? OffCopy { get; }

    /// <summary>
    /// Installed and nothing to do: loaded, or switched on and waiting for the next start. A turned-off copy is not:
    /// its switch is drawn and checking it means "turn it on" (<see cref="TurnedOff"/>).
    /// </summary>
    public bool Installed => InstalledVersion != null && !TurnedOff;

    /// <summary>JOINTURNON: installed but turned off by the player; checking the row turns it on (no download).</summary>
    public bool TurnedOff => OffCopy != null && !OffCopy.TurnsOnAtNextStart && !OffCopy.PausedHere;
}

/// <summary>Everything the join pages are built from, plain values so the model is testable.</summary>
internal sealed class JoinFacts
{
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";

    /// <summary>What the server asked this client (<c>Handshake.CopyServerAsked</c>).</summary>
    public IReadOnlyList<Handshake.ModRecord> Asked { get; set; } = Array.Empty<Handshake.ModRecord>();

    /// <summary>What this client answers with (<c>Handshake.CopyProvided</c>).</summary>
    public IReadOnlyList<Handshake.ModRecord> Provided { get; set; } = Array.Empty<Handshake.ModRecord>();

    /// <summary>The signed catalog body of this join (narrowed on a refusal), or null when none arrived.</summary>
    public ManifestBody? Catalog { get; set; }

    /// <summary>The verified join section, or null.</summary>
    public JoinSection? Section { get; set; }

    /// <summary>The join was refused (hard kick) or deferred into an install window.</summary>
    public bool Refused { get; set; }

    /// <summary>The server opened an install window for this join (<c>Ok = 2</c>).</summary>
    public bool InstallWindow { get; set; }

    /// <summary>
    /// The Progenitor Core capabilities the server's refusal named (<see cref="JoinCoreRefusal.Parse"/> of the kick
    /// reason). Empty unless the server refused this client's Core; no mod row can fix these.
    /// </summary>
    public IReadOnlyList<JoinCoreRequirement> CoreRequirements { get; set; } = Array.Empty<JoinCoreRequirement>();

    /// <summary>This client's own Progenitor version, for the Core refusal's "you have" (empty when unknown).</summary>
    public string OwnCoreVersion { get; set; } = "";

    /// <summary>
    /// The Progenitor release a click on Update Progenitor would install now (the self-update's offer: clickable, not held,
    /// no update running), or null. Decides whether a Core refusal offers the button.
    /// </summary>
    public string? UpdateVersion { get; set; }

    /// <summary>A loaded mod's readable name, or null.</summary>
    public Func<string, string?>? NameOf { get; set; }

    /// <summary>A loaded mod's version on this client, or null when it is not loaded.</summary>
    public Func<string, string?>? LoadedVersionOf { get; set; }

    /// <summary>
    /// JOINFLOW2: the Thunderstore version lists of the packages this join names (package → its versions), or
    /// null while they are not read. Decides "Use version X" and the version-gone answer.
    /// </summary>
    public IReadOnlyDictionary<string, Install.PackageVersions>? ThunderstoreVersions { get; set; }

    /// <summary>
    /// JOINFLOW4: a mod's kept versions against this join's tables (id, the server's request, this client's answer), or
    /// null. Decides "Use kept X" and the kept-for-other-servers lines.
    /// </summary>
    public Func<string, IReadOnlyList<Handshake.ModRecord>, IReadOnlyList<Handshake.ModRecord>, JoinKeptInfo?>? Kept { get; set; }

    /// <summary>
    /// JOINTURNON: the player's own turned-off copy of a mod (the Mods list's switch state), or null when the mod is not
    /// installed-but-off (not installed, running, or only paused for a server). Null: no row offers Turn on.
    /// </summary>
    public Func<string, JoinOffCopy?>? OffCopyOf { get; set; }

    /// <summary>
    /// LOCALJOIN1 (debug): plugin GUID → the local test index's listing that provides it. Null (the default, and
    /// always while <c>[Install] LocalIndexPath</c> is empty): no row ever uses a local source.
    /// </summary>
    public Func<string, Install.LocalTestMatch?>? LocalTestOf { get; set; }

    /// <summary>
    /// CLIENTPOLICY2: the mods this client runs now, as its report to the server lists them (GUID, name, version, source,
    /// package). Null: unknown (required client rows fall back to <see cref="LoadedVersionOf"/>; no not-allowed rows).
    /// </summary>
    public IReadOnlyList<ClientModEntry>? ClientMods { get; set; }

    /// <summary>
    /// Mod compatibility C3: this client's own effective range for (mod id, version): the copy at that version, so a
    /// turned-off copy or a pending install is never judged with the loaded copy's range; null when not known. With the catalog's Version matching, per-mod rules and ranges it rebuilds the server's judge
    /// (<see cref="VersionMatching.ClientContextFor"/>), so a version the server accepts is ready. Null: every mod gets the
    /// default range.
    /// </summary>
    public Func<string, string, string?>? OwnRangeOf { get; set; }

    /// <summary>CLIENTPOLICY2: the mods the per-server pause holds for this server (not in the report), or null.</summary>
    public IReadOnlyList<ClientModEntry>? PausedHere { get; set; }

    /// <summary>
    /// Mod compatibility C4: this client's own Progenitor (its Core version and sha256, how it was installed, where the
    /// server's build can come from). Null: no Progenitor row is drawn even when the catalog requires a build.
    /// </summary>
    public ProgenitorExact.ClientFacts? Progenitor { get; set; }

    /// <summary>CLIENTPOLICY2: Bootstrap's reloader can pause this plugin now (hot-loaded, no <c>ctx.UnsafeHarmony</c>).</summary>
    public Func<string, bool>? Pausable { get; set; }

    /// <summary>CLIENTPOLICY2: the player switched this mod off in the Mods list and it goes at the next start.</summary>
    public Func<string, bool>? TurnsOffAtRestart { get; set; }
}

/// <summary>The join pages' model: the required rows with their actions, the recommended rows and the counts.</summary>
internal sealed class JoinPageModel
{
    private JoinPageModel(JoinFacts facts, List<JoinRequiredRow> required, List<JoinRecommendedRow> recommended, List<JoinNotAllowedRow> notAllowed)
    {
        Facts = facts;
        Required = required;
        Recommended = recommended;
        NotAllowed = notAllowed;
    }

    public JoinFacts Facts { get; }
    public IReadOnlyList<JoinRequiredRow> Required { get; }
    public IReadOnlyList<JoinRecommendedRow> Recommended { get; }

    /// <summary>
    /// CLIENTPOLICY2: the red band's "Not allowed on this server" rows (Only approved): loaded mods the server's policy does
    /// not allow, and the ones this server's pause holds. Empty without a policy or without this client's mod list.
    /// </summary>
    public IReadOnlyList<JoinNotAllowedRow> NotAllowed { get; }

    /// <summary>CLIENTPOLICY2: not-allowed mods still loaded that the player can still fix here (pause, or turn off and restart).</summary>
    public int NotAllowedUnresolved
    {
        get
        {
            int n = 0;
            foreach (var r in NotAllowed) if (r.Unresolved) n++;
            return n;
        }
    }

    /// <summary>CLIENTPOLICY2: how many required rows are the admins' required client mods.</summary>
    public int ClientRequiredCount
    {
        get
        {
            int n = 0;
            foreach (var r in Required) if (r.Origin == JoinRowOrigin.ClientPolicy) n++;
            return n;
        }
    }

    public JoinNotAllowedRow? FindNotAllowed(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var r in NotAllowed) if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    public int RequiredReady
    {
        get
        {
            int n = 0;
            foreach (var r in Required) if (r.Ready) n++;
            return n;
        }
    }

    /// <summary>The server refused this client's Progenitor Core for a capability it lacks (no mod row says so).</summary>
    public bool CoreRefused => Facts.CoreRequirements is { Count: > 0 };

    /// <summary>
    /// A Core refusal for an out-of-date Progenitor that the self-update can fix here: it offers a release newer than this
    /// client's and at least the server's version (when the refusal named it).
    /// </summary>
    public bool CanUpdateProgenitor
    {
        get
        {
            if (!CoreRefused || !JoinText.CoreOutOfDate(Facts.CoreRequirements)) return false;
            string offered = (Facts.UpdateVersion ?? "").Trim();
            if (offered.Length == 0) return false;
            string server = JoinText.CoreRefusedServerVersion(Facts.CoreRequirements);
            if (server.Length > 0 && Install.UpdateVersions.Compare(offered, server) < 0) return false;
            string own = (Facts.OwnCoreVersion ?? "").Trim();
            return own.Length == 0 || Install.UpdateVersions.Compare(offered, own) > 0;
        }
    }

    /// <summary>
    /// Required rows that are not ready, plus (CLIENTPOLICY2) not-allowed mods still to fix, plus one for a Core
    /// refusal: the join cannot succeed until Core changes, whatever the rows say.
    /// </summary>
    public int NeedAttention => Required.Count - RequiredReady + NotAllowedUnresolved + (CoreRefused ? 1 : 0);

    public bool AllRequiredReady => NeedAttention == 0;

    public string ServerName => JoinText.ServerLabel(Facts.ServerName, Facts.Host);

    /// <summary>
    /// Build the model. The required list is <b>every</b> <c>ServerMustHave</c> row the server asked about,
    /// matching or not, plus every row the server's own join check rejects (a <c>NoNeedForSync</c> row at a
    /// refused version), plus this client's own requirements the server lacks. Status is
    /// <c>Handshake.Compare</c>'s verdict, never the catalog's. The catalog only adds where a row can be
    /// fetched from; it never adds a row.
    /// </summary>
    public static JoinPageModel Build(JoinFacts facts)
    {
        facts ??= new JoinFacts();
        var asked = facts.Asked ?? Array.Empty<Handshake.ModRecord>();
        var provided = facts.Provided ?? Array.Empty<Handshake.ModRecord>();
        if (!facts.Refused && !facts.InstallWindow)
        {
            // Live 2026-10-04: in the world, the join-time tables go stale when either side hot-swaps a mod
            // (both moved to 2026.10.52 but the row read "Needs 2026.10.50"). The signed section carries the
            // versions the server's handshake requires now (not the catalog rows, which an operator overlay
            // can pin) and this client's loaded plugins give its own; the version rule stays the server's, so
            // Handshake.Compare judges exactly as the handshake would now.
            asked = CurrentAsked(asked, facts.Section);
            provided = CurrentProvided(provided, facts.LoadedVersionOf);
        }

        // Mod compatibility C3: judge each version as the server's join check did (its Version matching, per-mod rules
        // and the newer side's range, from its catalog), not by the row's attribute rule. Null without a catalog that
        // carries them: the request rows' rules decide, as before.
        var judge = JudgeFor(facts, asked, provided);
        Handshake.Compare.Evaluate(asked, provided, out var failedIds, judge);
        var failed = new HashSet<string>(failedIds, StringComparer.OrdinalIgnoreCase);
        var rows = new List<JoinRequiredRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Mod compatibility C4: under Exact version for all the server holds players to its own Progenitor build (the
        // catalog's requirement); the row comes first.
        if (ProgenitorRow(facts) is { } progenitorRow)
        {
            rows.Add(progenitorRow);
            seen.Add(progenitorRow.Id);
        }

        for (int i = 0; i < asked.Count; i++)
        {
            var rec = asked[i];
            if (string.IsNullOrEmpty(rec.Guid) || Handshake.Compare.IsReservedGuid(rec.Guid)) continue;
            bool required = rec.Level == NetworkCompatibilityLevel.ServerMustHave;
            if (!required && !failed.Contains(rec.Guid)) continue;
            if (!seen.Add(rec.Guid)) continue;
            string? have = HaveVersion(provided, rec.Guid);
            string? problem = Handshake.Compare.EnforcedKickReason(rec.Guid, asked, provided, judge);
            JoinRowStatus status = problem == null
                ? JoinRowStatus.Ready
                : have == null ? JoinRowStatus.Missing
                : CompareVersions(have, rec.Version) > 0 ? JoinRowStatus.Newer : JoinRowStatus.Older;
            // JOINTURNON: a copy the player turned off is installed but not loaded, so the handshake does not see it. When
            // that copy passes the server's own check (the same Compare, with the off copy in this client's answer), the
            // row is "Turned off" and offers Turn on instead of a download.
            var off = status == JoinRowStatus.Ready ? null : OffCopyFor(facts, rec.Guid);
            // A per-server pause is not the player's choice: the required row keeps the pause's own behavior.
            if (off is { PausedHere: true }) off = null;
            if (off != null && OffCopySatisfies(rec.Guid, off, asked, provided, judge)) status = JoinRowStatus.TurnedOff;
            var row = NewRow(facts, rec.Guid, JoinRowOrigin.ServerRequires, rec.Version, have, status, asked, provided, rec.VersionRule, off);
            row.MatchRule = MatchRuleOf(judge, rec.Guid);
            rows.Add(row);
        }

        // The reverse walk: this client's own ServerMustHave rows the server failed (Evaluate's second pass).
        for (int i = 0; i < failedIds.Count; i++)
        {
            string id = failedIds[i];
            if (string.IsNullOrEmpty(id) || seen.Contains(id)) continue;
            seen.Add(id);
            rows.Add(NewRow(facts, id, JoinRowOrigin.YouRequire, HaveVersion(provided, id) ?? "", HaveVersion(provided, id),
                JoinRowStatus.ServerLacks, asked, provided));
        }

        // CLIENTPOLICY2: the admins' required client mods (the signed section's requiredClient), judged with their rule
        // against this client's mods. A mod the server's own request already covers keeps that row.
        if (facts.Section != null)
            foreach (var p in facts.Section.RequiredClient)
            {
                if (p == null || p.Key.Length == 0 || seen.Contains(p.Key) || (p.Id.Length > 0 && seen.Contains(p.Id))) continue;
                seen.Add(p.Key);
                rows.Add(PolicyRow(facts, p));
            }

        // Needs attention first, then ready; each group keeps the server's order.
        var ordered = new List<JoinRequiredRow>(rows.Count);
        foreach (var r in rows) if (!r.Ready) ordered.Add(r);
        foreach (var r in rows) if (r.Ready) ordered.Add(r);

        var recommended = new List<JoinRecommendedRow>();
        var section = facts.Section;
        if (section != null)
        {
            foreach (var mod in section.Recommended)
            {
                if (mod == null) continue;
                string? installed = null;
                JoinOffCopy? off = null;
                if (mod.Id.Length > 0)
                {
                    try { installed = facts.LoadedVersionOf?.Invoke(mod.Id); }
                    catch { installed = null; }
                    installed ??= HaveVersion(provided, mod.Id);
                    // JOINTURNON: a recommended mod the player turned off is installed: its switch turns it on, no download.
                    // A restart-only mod skipped at this start is still in the chainloader (LoadedVersionOf answers), but off.
                    off = OffCopyFor(facts, mod.Id);
                }

                recommended.Add(new JoinRecommendedRow(mod, NameFor(facts, mod.Id, mod.Package, plainId: true), installed, off));
            }
        }

        // CLIENTPOLICY2: the loaded mods an Only approved server does not allow (the server's own rule 2, on the facts this
        // client has: the request, the signed run list, required and recommended rows).
        var notAllowed = ClientPolicyView.NotAllowedRows(facts.Section, facts.Asked, facts.ClientMods, facts.PausedHere,
            facts.Pausable, facts.TurnsOffAtRestart);
        return new JoinPageModel(facts, ordered, recommended, notAllowed);
    }

    /// <summary>
    /// C3/C4 wording: how the server judges <paramref name="id"/> when not by ranges, from the judge rebuilt from its catalog
    /// (an admins' per-mod rule, else Exact or Same minor for all), or null (declared ranges, or no catalog that says).
    /// </summary>
    internal static ClientModRule? MatchRuleOf(VersionJudgeContext? judge, string id)
    {
        var settings = judge?.Settings;
        if (settings == null) return null;
        if (settings.TryRule(id, out var rule)) return rule;
        return settings.Mode switch
        {
            VersionMatchingMode.Exact => ClientModRule.Exact,
            VersionMatchingMode.Minor => ClientModRule.Minor,
            _ => null,
        };
    }

    /// <summary>
    /// Mod compatibility C4: the Progenitor row when the join's catalog carries the server's Progenitor requirement and this
    /// client's own facts are known; null otherwise.
    /// </summary>
    internal static JoinRequiredRow? ProgenitorRow(JoinFacts facts)
    {
        var req = ProgenitorExact.RequirementOf(facts?.Catalog);
        if (req == null || facts!.Progenitor == null) return null;
        var own = facts.Progenitor;
        var d = ProgenitorExact.Decide(req, own);
        string have = (own.CoreVersion ?? "").Trim();
        return new JoinRequiredRow(ProgenitorExact.RowId, ProgenitorExact.Name, JoinRowOrigin.Progenitor, req.Version,
            have.Length > 0 ? have : null, d.Status,
            d.Source == ProgenitorExact.FixSource.Thunderstore ? JoinRowSource.Thunderstore : JoinRowSource.None,
            null, 0, req.Sha, false, d.Action, d.Status == JoinRowStatus.Newer ? JoinRowTags.Ahead : JoinRowTags.None)
        {
            MatchRule = ClientModRule.Exact,
            ProgenitorNote = d.Note,
            ProgenitorSameVersion = d.SameVersion,
        };
    }

    /// <summary>
    /// Mod compatibility C3: the server's judge as this client can rebuild it from the join's catalog
    /// (<see cref="VersionMatching.ClientContextFor"/>), or null when the catalog does not carry the server's Version
    /// matching.
    /// </summary>
    internal static VersionJudgeContext? JudgeFor(JoinFacts facts, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided)
    {
        try { return VersionMatching.ClientContextFor(facts.Catalog, asked, facts.OwnRangeOf); }
        catch { return null; }
    }

    /// <summary>
    /// CLIENTPOLICY2: one required client mod row. Its status is the server's rule 1 on this client's mods: found by plugin
    /// GUID, else by package (the install ledger); <see cref="ClientModRules.Passes"/> with the admins' rule (the stage 1
    /// <c>AtLeast</c> included). Its actions are the Thunderstore ones only (Install / Update / Use version X), Turn on for
    /// a copy the player turned off, else Ask / Tell the admin: never a server's copy.
    /// </summary>
    private static JoinRequiredRow PolicyRow(JoinFacts facts, ClientModRow p)
    {
        var rule = ClientModRules.Effective(p.Rule, p.Version);
        ClientModEntry? entry = null;
        if (facts.ClientMods != null)
        {
            var mine = new ClientModReport();
            foreach (var e in facts.ClientMods) if (e != null) mine.Entries.Add(e);
            entry = mine.Find(p);
        }
        else if (p.Id.Length > 0)
        {
            string? loaded = null;
            try { loaded = facts.LoadedVersionOf?.Invoke(p.Id); }
            catch { loaded = null; }
            if (loaded != null) entry = new ClientModEntry(p.Id, "", loaded == "?" ? "" : loaded, ClientModSources.Chainloader);
        }

        JoinRowStatus status;
        string? have = entry == null ? null : entry.Version.Length > 0 ? entry.Version : "?";
        if (entry == null) status = JoinRowStatus.Missing;
        else if (ClientModRules.Passes(rule, p.Version, entry.Version)) status = JoinRowStatus.Ready;
        else
        {
            int? order = entry.Version.Length == 0 ? -1 : Handshake.Compare.CompareOrder(entry.Version, p.Version);
            status = order > 0 ? JoinRowStatus.Newer : JoinRowStatus.Older;
        }

        // JOINTURNON: a copy the player turned off that passes the admins' rule is turned on, never downloaded again.
        JoinOffCopy? off = null;
        if (status != JoinRowStatus.Ready && p.Id.Length > 0)
        {
            off = OffCopyFor(facts, p.Id);
            if (off is { PausedHere: true }) off = null;
            if (off != null && (rule == ClientModRule.Any || off.Version.Length > 0) && ClientModRules.Passes(rule, p.Version, off.Version))
                status = JoinRowStatus.TurnedOff;
        }

        string? package = p.Package.Length > 0 ? p.Package : null;
        // The Thunderstore lookup's rule: Exact and Same minor as the handshake's; "or newer" and "Any version" take the
        // named version when it is listed, else the newest (NetworkVersionRule.None).
        var lookupRule = rule switch
        {
            ClientModRule.Exact => NetworkVersionRule.Exact,
            ClientModRule.Minor => NetworkVersionRule.Minor,
            _ => NetworkVersionRule.None,
        };
        var availability = package == null || facts.ThunderstoreVersions == null
            ? Install.PackageVersionAvailability.Unknown
            : Install.ThunderstoreVersions.Availability(facts.ThunderstoreVersions, package, p.Version, lookupRule);
        var policySource = package != null ? JoinRowSource.Thunderstore : JoinRowSource.None;
        // LOCALJOIN1 (debug): the same last resort for a required client mod named by plugin id.
        string policyLocalNote = "";
        if (LocalTestFor(facts, p.Id, status, package, availability) is { } localPolicy)
        {
            if (p.Version.Length > 0 && Install.ThunderstoreVersions.Resolve(localPolicy.Active, p.Version, lookupRule, out _) == null)
                policyLocalNote = Install.LocalTestSource.WrongVersion(localPolicy.Version, p.Version);
            else
            {
                package = localPolicy.Key;
                policySource = JoinRowSource.LocalTest;
                availability = Install.PackageVersionAvailability.Unknown;
            }
        }

        var action = ActionFor(JoinRowOrigin.ClientPolicy, status, package != null, availability);
        var tags = JoinRowTags.None;
        if (action == JoinRowAction.TurnOn)
            tags |= off is { AppliesNow: true, TurnsOnAtNextStart: false } ? JoinRowTags.AppliesNow : JoinRowTags.NeedsRestart;
        string latest = "";
        if (package != null && facts.ThunderstoreVersions != null && facts.ThunderstoreVersions.TryGetValue(package, out var pv)) latest = pv.Latest;

        string name = p.Name;
        if (name.Length == 0 && p.Id.Length > 0)
        {
            try { name = facts.NameOf?.Invoke(p.Id) ?? ""; }
            catch { name = ""; }
        }

        if (name.Trim().Length == 0) name = package != null ? Install.InstallSummary.NameOf(package) : p.Key;
        return new JoinRequiredRow(p.Key, name.Trim(), JoinRowOrigin.ClientPolicy, p.Version, have, status,
            policySource, package, 0, null, false, action, tags)
        {
            Availability = availability,
            ThunderstoreLatest = latest,
            VersionRule = lookupRule,
            OffCopy = off,
            Reason = p.Reason,
            PolicyRule = rule,
            ModId = p.Id,
            LocalTestNote = policyLocalNote,
        };
    }

    /// <summary>The server's request with each version its signed section states now (rule and level kept).</summary>
    internal static IReadOnlyList<Handshake.ModRecord> CurrentAsked(IReadOnlyList<Handshake.ModRecord> asked, JoinSection? section)
    {
        if (section == null || section.Versions.Count == 0 || asked.Count == 0) return asked;
        var list = new List<Handshake.ModRecord>(asked.Count);
        foreach (var rec in asked)
        {
            string? now = section.VersionOf(rec.Guid);
            list.Add(!string.IsNullOrEmpty(now) && !string.Equals(now, rec.Version, StringComparison.Ordinal)
                ? new Handshake.ModRecord(rec.Guid, now!, rec.Level, rec.VersionRule, rec.Declared)
                : rec);
        }

        return list;
    }

    /// <summary>This client's answer with each version as loaded now (a mod no longer loaded keeps its record).</summary>
    internal static IReadOnlyList<Handshake.ModRecord> CurrentProvided(IReadOnlyList<Handshake.ModRecord> provided, Func<string, string?>? loadedVersionOf)
    {
        if (loadedVersionOf == null || provided.Count == 0) return provided;
        var list = new List<Handshake.ModRecord>(provided.Count);
        foreach (var rec in provided)
        {
            string? now = null;
            try { now = loadedVersionOf(rec.Guid); }
            catch { now = null; }
            list.Add(!string.IsNullOrEmpty(now) && now != "?" && !string.Equals(now, rec.Version, StringComparison.Ordinal)
                ? new Handshake.ModRecord(rec.Guid, now!, rec.Level, rec.VersionRule, rec.Declared)
                : rec);
        }

        return list;
    }

    /// <summary>
    /// LOCALJOIN1 (debug): the local test listing for a missing or older mod the server gave no usable source for (no
    /// package, or one whose version Thunderstore no longer serves); null while no local index is configured.
    /// </summary>
    private static Install.LocalTestMatch? LocalTestFor(JoinFacts facts, string id, JoinRowStatus status, string? package,
        Install.PackageVersionAvailability availability)
    {
        if (facts.LocalTestOf == null || string.IsNullOrEmpty(id)) return null;
        if (status is not (JoinRowStatus.Missing or JoinRowStatus.Older)) return null;
        bool usable = package != null
            && availability is not (Install.PackageVersionAvailability.VersionGone or Install.PackageVersionAvailability.NotListed);
        if (usable) return null;
        try { return facts.LocalTestOf(id); }
        catch { return null; }
    }

    /// <summary>JOINTURNON: the player's turned-off copy of <paramref name="id"/>, or null (no probe, or the probe threw).</summary>
    private static JoinOffCopy? OffCopyFor(JoinFacts facts, string id)
    {
        if (facts.OffCopyOf == null || string.IsNullOrEmpty(id)) return null;
        try { return facts.OffCopyOf(id); }
        catch { return null; }
    }

    /// <summary>
    /// JOINTURNON: whether the server's own join check would accept <paramref name="off"/>: <c>Handshake.Compare</c> on the
    /// server's request and this client's answer with the off copy's version in place (the server's level and version
    /// rule decide, exactly as the handshake would once the copy is on). An off copy of unknown version never passes.
    /// </summary>
    internal static bool OffCopySatisfies(string id, JoinOffCopy? off, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided,
        VersionJudgeContext? judge = null)
    {
        if (off == null || string.IsNullOrWhiteSpace(off.Version) || string.IsNullOrEmpty(id)) return false;
        var answer = new List<Handshake.ModRecord>((provided?.Count ?? 0) + 1);
        bool replaced = false;
        foreach (var rec in provided ?? Array.Empty<Handshake.ModRecord>())
        {
            if (!replaced && string.Equals(rec.Guid, id, StringComparison.OrdinalIgnoreCase))
            {
                answer.Add(new Handshake.ModRecord(rec.Guid, off.Version.Trim(), rec.Level, rec.VersionRule, rec.Declared));
                replaced = true;
            }
            else answer.Add(rec);
        }

        if (!replaced) answer.Add(Handshake.ModRecord.CreateDeclared(id, off.Version.Trim(), NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact));
        try { return Handshake.Compare.EnforcedKickReason(id, asked, answer, judge) == null; }
        catch { return false; }
    }

    private static JoinRequiredRow NewRow(JoinFacts facts, string id, JoinRowOrigin origin, string required, string? have, JoinRowStatus status,
        IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided,
        NetworkVersionRule rule = NetworkVersionRule.Exact, JoinOffCopy? off = null)
    {
        JoinKeptInfo? kept = null;
        try { kept = facts.Kept?.Invoke(id, asked, provided); }
        catch { kept = null; }
        var mod = FindCatalogRow(facts.Catalog, id);
        string? package = mod?.Package;
        var source = package != null ? JoinRowSource.Thunderstore : JoinRowSource.None;
        bool hot = mod?.HotLoadable ?? false;
        var availability = package == null
            ? Install.PackageVersionAvailability.Unknown
            : facts.ThunderstoreVersions == null
                ? Install.PackageVersionAvailability.Unknown
                : Install.ThunderstoreVersions.Availability(facts.ThunderstoreVersions, package, required, rule);
        // LOCALJOIN1 (debug): no usable source from the server, and the configured local test index provides it.
        long size = mod?.Size ?? 0;
        string? sha = mod?.Sha256;
        string localNote = "";
        if (origin == JoinRowOrigin.ServerRequires && LocalTestFor(facts, id, status, package, availability) is { } local)
        {
            // Offered only when the local listing serves a version the server accepts (the installer's own pick).
            if (Install.ThunderstoreVersions.Resolve(local.Active, required, rule, out _) == null && (required ?? "").Length > 0)
                localNote = Install.LocalTestSource.WrongVersion(local.Version, required ?? "");
            else
            {
                package = local.Key;
                source = JoinRowSource.LocalTest;
                availability = Install.PackageVersionAvailability.Unknown;
                // The facts the row shows are the local package's, not the server catalog entry's.
                size = local.Size;
                sha = null;
                hot = local.HotLoadable;
            }
        }

        var action = ActionFor(origin, status, package != null, availability);
        // JOINFLOW4: a kept copy the server's check accepts needs no download and wins over every other source but the
        // player's own turned-off copy (JOINTURNON: Turn on comes first, see ActionFor).
        if (status != JoinRowStatus.Ready && status != JoinRowStatus.TurnedOff && origin == JoinRowOrigin.ServerRequires && kept is { PickUsable: true })
            action = JoinRowAction.UseKept;
        var tags = JoinRowTags.None;
        if (status == JoinRowStatus.Newer) tags |= JoinRowTags.Ahead;
        if (action == JoinRowAction.TurnOn)
            tags |= off is { AppliesNow: true, TurnsOnAtNextStart: false } ? JoinRowTags.AppliesNow : JoinRowTags.NeedsRestart;
        else if (action == JoinRowAction.UseKept)
            tags |= kept!.PickRoute == "now" ? JoinRowTags.AppliesNow : JoinRowTags.NeedsRestart;
        else if (action is JoinRowAction.Install or JoinRowAction.Update or JoinRowAction.UseVersion)
            tags |= AppliesNow(action, hot, kept) ? JoinRowTags.AppliesNow : JoinRowTags.NeedsRestart;
        string latest = "";
        if (package != null && facts.ThunderstoreVersions != null && facts.ThunderstoreVersions.TryGetValue(package, out var pv)) latest = pv.Latest;
        return new JoinRequiredRow(id, NameFor(facts, id, package), origin, required ?? "", have, status, source, package,
            size, sha, hot, action, tags)
        {
            Availability = availability,
            ThunderstoreLatest = latest,
            VersionRule = rule,
            Kept = kept,
            LocalTestNote = localNote,
            OffCopy = origin == JoinRowOrigin.ServerRequires ? off : null,
        };
    }

    /// <summary>
    /// The one action for a row (docs/join-flow.md, "Row actions"): ready rows have none; a Thunderstore
    /// package installs or updates it; else the admins are asked (or, when this client is ahead, told). A mod
    /// only this client requires is turned off. Servers never send mod files (removed 2026-10-05), so there is
    /// no "server's copy" action.
    /// </summary>
    internal static JoinRowAction ActionFor(JoinRowOrigin origin, JoinRowStatus status, bool hasPackage) =>
        ActionFor(origin, status, hasPackage, Install.PackageVersionAvailability.Unknown);

    /// <summary>
    /// JOINFLOW2: the same with what Thunderstore serves. Being ahead offers "Use version X"
    /// only when Thunderstore still serves the server's exact version (else "Tell the admin"); an Install or
    /// Update whose exact version is gone (or whose package is not listed) becomes "Ask the admin". While the
    /// version lists are not read (<see cref="Install.PackageVersionAvailability.Unknown"/>) the stage 1 answer stands.
    ///
    /// <para>JOINTURNON, the order of precedence and why:</para>
    /// <list type="number">
    /// <item><b>Ready</b>: the server's check already accepts this client; nothing to offer.</item>
    /// <item><b>Turn off</b> (a mod only this client requires, the server lacks it): the requirement is the player's own,
    /// so no copy of the mod can fix it; only turning it off can.</item>
    /// <item><b>Turn on</b> (<see cref="JoinRowStatus.TurnedOff"/>: the player's own copy is installed but switched off,
    /// and that copy passes the server's version rule): the cheapest fix that changes nothing on disk and downloads
    /// nothing, and the only one that respects the player's install. It must win over Use kept and every download: an
    /// Install would fetch a copy the player already has (and leave it switched off). An off copy at the wrong version
    /// is not <see cref="JoinRowStatus.TurnedOff"/>, so the chain below answers it as before (and the fix turns it on).</item>
    /// <item>The existing chain: Use kept (JOINFLOW4, applied by the caller), Use version, Install / Update, Ask / Tell
    /// the admin.</item>
    /// </list>
    /// </summary>
    internal static JoinRowAction ActionFor(JoinRowOrigin origin, JoinRowStatus status, bool hasPackage,
        Install.PackageVersionAvailability availability)
    {
        bool gone = availability is Install.PackageVersionAvailability.VersionGone or Install.PackageVersionAvailability.NotListed;
        if (status == JoinRowStatus.Ready) return JoinRowAction.Ready;
        if (origin == JoinRowOrigin.YouRequire || status == JoinRowStatus.ServerLacks) return JoinRowAction.TurnOff;
        if (status == JoinRowStatus.TurnedOff) return JoinRowAction.TurnOn;
        if (status == JoinRowStatus.Newer)
            return hasPackage && availability == Install.PackageVersionAvailability.Available ? JoinRowAction.UseVersion
                : JoinRowAction.TellAdmin;
        if (hasPackage && !gone) return status == JoinRowStatus.Missing ? JoinRowAction.Install : JoinRowAction.Update;
        return JoinRowAction.AskAdmin;
    }

    /// <summary>
    /// Whether the fix takes effect in this session. A Thunderstore install applies now when the signed catalog
    /// says the mod hot-loads; "Use version X" replaces a copy that is loaded: JOINFLOW4 keeps that copy and, when
    /// the reloader can unload it now, the server's version can load this session too; otherwise it is a restart
    /// install.
    /// </summary>
    private static bool AppliesNow(JoinRowAction action, bool hotLoadable, JoinKeptInfo? kept = null) =>
        action == JoinRowAction.UseVersion ? hotLoadable && kept is { KeepNow: true } : hotLoadable;

    /// <summary>The rows the Install button fixes: required rows with an install action, and checked recommended rows not installed.</summary>
    public IReadOnlyList<string> InstallTargets(ICollection<string>? checkedRecommended) => InstallTargets(checkedRecommended, null);

    /// <summary>
    /// JOINFLOW2: the same, leaving out rows a batch already handled or is handling (<paramref name="settled"/>
    /// answers true for a key that is queued, running, loaded or waiting for the restart).
    /// </summary>
    public IReadOnlyList<string> InstallTargets(ICollection<string>? checkedRecommended, Func<string, bool>? settled)
    {
        var list = new List<string>();
        foreach (var r in Required)
            if (r.Thunderstore && !IsSettled(settled, r.Id)) list.Add(r.Id);
        if (checkedRecommended != null)
            foreach (var r in Recommended)
                if (!r.Installed && !r.TurnedOff && Contains(checkedRecommended, KeyOf(r)) && !list.Contains(KeyOf(r)) && !IsSettled(settled, KeyOf(r)))
                    list.Add(KeyOf(r));
        return list;
    }

    /// <summary>
    /// JOINTURNON: the checked recommended rows whose mod is installed but turned off: the bottom button turns them on
    /// (no download, no Thunderstore confirmation of their own). <c>Install N mods</c> never counts them.
    /// </summary>
    public IReadOnlyList<JoinRecommendedRow> TurnOnTargets(ICollection<string>? checkedRecommended)
    {
        var list = new List<JoinRecommendedRow>();
        if (checkedRecommended == null) return list;
        foreach (var r in Recommended)
            if (r.TurnedOff && r.Id.Length > 0 && Contains(checkedRecommended, KeyOf(r))) list.Add(r);
        return list;
    }

    private static bool IsSettled(Func<string, bool>? settled, string key)
    {
        if (settled == null) return false;
        try { return settled(key); }
        catch { return false; }
    }

    /// <summary>The key a recommended row is checked by: its id, else its package.</summary>
    public static string KeyOf(JoinRecommendedRow row) => row.Id.Length > 0 ? row.Id : row.Mod.Package;

    public JoinRequiredRow? FindRequired(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var r in Required) if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    public JoinRecommendedRow? FindRecommended(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var r in Recommended)
            if (string.Equals(KeyOf(r), key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Mod.Package, key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>The names of the rows nothing on this page can fix (the admins have to).</summary>
    public IReadOnlyList<string> CannotFixHere()
    {
        var list = new List<string>();
        foreach (var r in Required)
            if (r.Action is JoinRowAction.AskAdmin or JoinRowAction.TellAdmin) list.Add(r.Name);
        return list;
    }

    private static ManifestMod? FindCatalogRow(ManifestBody? body, string id)
    {
        if (body?.Mods == null) return null;
        foreach (var m in body.Mods)
            if (m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    private static string? HaveVersion(IReadOnlyList<Handshake.ModRecord>? rows, string id)
    {
        if (rows == null) return null;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Guid, id, StringComparison.OrdinalIgnoreCase)) continue;
            return string.IsNullOrEmpty(rows[i].Version) ? null : rows[i].Version;
        }

        return null;
    }

    private static bool Contains(ICollection<string>? set, string id)
    {
        if (set == null) return false;
        foreach (var s in set) if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <param name="plainId">
    /// JOINADMIN1 live fix (2026-10-04): a recommended mod with no known name and no package showed the last segment
    /// of its id (<c>test.client.mod</c> read "Mod"). Recommended rows fall back to the whole id.
    /// </param>
    private static string NameFor(JoinFacts facts, string id, string? package, bool plainId = false)
    {
        try
        {
            string? known = string.IsNullOrEmpty(id) ? null : facts.NameOf?.Invoke(id);
            if (!string.IsNullOrWhiteSpace(known)) return known!.Trim();
        }
        catch { /* fall through */ }

        // The server's own name for a mod this client lacks (the signed join section's names).
        string? named = facts.Section?.NameOf(id);
        if (!string.IsNullOrWhiteSpace(named)) return named!.Trim();
        if (!string.IsNullOrWhiteSpace(package)) return Install.InstallSummary.NameOf(package!);
        string s = (id ?? "").Trim();
        if (plainId) return s;
        int dot = s.LastIndexOf('.');
        string last = dot >= 0 && dot + 1 < s.Length ? s.Substring(dot + 1) : s;
        return last.Length == 0 ? s : char.ToUpperInvariant(last[0]) + last.Substring(1);
    }

    /// <summary>
    /// Dotted numeric versions compared part by part (<c>1.10 &gt; 1.9</c>); a non-numeric part compares as
    /// text. Positive when <paramref name="a"/> is newer.
    /// </summary>
    internal static int CompareVersions(string? a, string? b)
    {
        string[] pa = (a ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
        string[] pb = (b ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            string x = i < pa.Length ? pa[i] : "0";
            string y = i < pb.Length ? pb[i] : "0";
            bool nx = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out long ix);
            bool ny = long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out long iy);
            int c = nx && ny ? ix.CompareTo(iy) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return Math.Sign(c);
        }

        return 0;
    }
}

/// <summary>Every string the join pages show (JOINFLOW1), game-free and unit-tested. US English.</summary>
internal static class JoinText
{
    public const string LeaveButton = "Leave";
    public const string ContinueButton = "Continue";
    public const string CloseButton = "Close";
    public const string BackToMessageButton = "← Server message";
    public const string BackButton = "Back";
    public const string DontShowAgain = "Don't show again until it changes";
    /// <summary>
    /// The line under the message box, drawn only when a token in the message produced a time (requested
    /// 2026-10-05: "From the server's admins" was redundant, and the line meant nothing without a time).
    /// </summary>
    public const string MessageTimesLine = "Times shown in your local time.";

    /// <summary>Whether the message page draws <see cref="MessageTimesLine"/>: a token in the message produced a time now.</summary>
    public static bool ShowsTimesLine(JoinSection? section, DateTime nowUtc, TimeZoneInfo? playerZone = null)
    {
        if (section == null || section.Message.IndexOf('{') < 0) return false;
        TimeZoneInfo serverZone;
        try { serverZone = section.Zone.Resolve(); }
        catch { serverZone = TimeZoneInfo.Utc; }
        ResetCountdown.Render(section.Message, section.Reset, nowUtc, serverZone, playerZone ?? TimeZoneInfo.Local, out _, out bool hasTime);
        return hasTime;
    }
    public const string ModsSubtitle = "You need all required mods to join. Recommended mods are optional.";
    public const string RequiredHeading = "Required to join";
    public const string RecommendedHeading = "Recommended by this server";
    public const string RecommendedSubheading = "Client mods · optional";
    public const string ReadyWord = "Ready";
    public const string InstalledWord = "Installed";
    public const string TrustOfferChanged = "The server's offer changed while this was open. Check it again before you download.";
    public const string ShowMessageButton = "Show";

    /// <summary>The message page's quiet way to the mods page when Continue would close (everything ready, nothing changed).</summary>
    public const string ViewModsButton = "View mods";

    public const string CopiedForAdmins = "Copied a message for the admins to your clipboard.";
    public const string ClipboardUnavailable = "Copying is not available here. Tell the admins: ";
    public const string NothingDownloaded = "Nothing was downloaded.";
    public const string NothingToInstall = "Nothing to install.";

    public static string MessageTitle(string? serverName) =>
        "Welcome to " + (string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName!.Trim());

    public static string ModsTitle(string? serverName) =>
        "Mods for " + (string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName!.Trim());

    /// <summary>
    /// The server a page title names: the name known now (<paramref name="liveName"/>), else the page model's name, else
    /// "this server" (reported 2026-10-06: a title read "Mods for 127.0.0.1:9976" while the server's name was known).
    /// </summary>
    public static string PageLabel(string? modelName, string? liveName, string? host) =>
        ServerLabel(!string.IsNullOrWhiteSpace(liveName) ? liveName : modelName, host);

    /// <summary>
    /// The server's name for the pages players read, else "this server": never its <c>address:port</c> (2026-10-19) nor a
    /// relay id (a Steam networking identity). <paramref name="host"/> is kept for callers; the trust page's Server fact
    /// (<c>JoinFlowPanel.ServerFact</c>) and the Server tab's Last server row show the address on purpose.
    /// </summary>
    public static string ServerLabel(string? name, string? host)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
        return "this server";
    }

    /// <summary>
    /// The server's name, else its address when it reads as one (<c>1.2.3.4:9876</c>, <c>host.example:9876</c>), else
    /// "this server": where the address is what tells two servers apart (the Last server row, logs).
    /// </summary>
    public static string NameOrAddress(string? name, string? host)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
        string h = (host ?? "").Trim();
        return LooksLikeAddress(h) ? h : "this server";
    }

    /// <summary>
    /// The name the screens use: the signed catalog's, unless it is the catalog's default ("V Rising", an
    /// operator who set no <c>serverName</c> on an older server), else the name this client knows for the
    /// joined server (its own folder's settings when the server runs on this PC), else empty.
    /// </summary>
    public static string PickServerName(string? catalogName, string? knownName)
    {
        string c = (catalogName ?? "").Trim();
        if (c.Length > 0 && !string.Equals(c, ModManifestJson.DefaultServerName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c, ModManifest.StockServerName, StringComparison.OrdinalIgnoreCase)) return c;
        string k = (knownName ?? "").Trim();
        if (string.Equals(k, ModManifest.StockServerName, StringComparison.OrdinalIgnoreCase)) k = "";
        return k.Length > 0 ? k : "";
    }

    /// <summary>
    /// An address the player would recognize: one with a port or a dot (<c>1.2.3.4:9876</c>, <c>[::1]:9876</c>,
    /// <c>lan-box:9876</c>, <c>host.example</c>) or <c>localhost</c>. A Steam relay identity has neither.
    /// </summary>
    internal static bool LooksLikeAddress(string host)
    {
        if (host.Length == 0) return false;
        return host.IndexOf(':') >= 0 || host.IndexOf('.') > 0 || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string Count(int n, string one, string many) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    /// <summary>
    /// <c>6 required mods, 3 recommended. 2 need attention.</c>; CLIENTPOLICY2 counts the admins' required client mods
    /// (<c>6 required mods (2 client)</c>) and the loaded mods the server does not allow (<c>1 mod isn't allowed here.</c>,
    /// which replaces <c>All ready.</c>): <c>6 required mods (2 client), 3 recommended. 1 mod isn't allowed here.</c>
    /// </summary>
    public static string Summary(JoinPageModel model)
    {
        if (model == null) return "";
        int client = model.ClientRequiredCount;
        string head = Count(model.Required.Count, "required mod", "required mods")
            + (client > 0 ? " (" + client.ToString(CultureInfo.InvariantCulture) + " client)" : "")
            + (model.Recommended.Count > 0 ? ", " + model.Recommended.Count.ToString(CultureInfo.InvariantCulture) + " recommended" : "")
            + ".";
        int na = model.NotAllowedUnresolved;
        string notAllowed = na == 0 ? "" : " " + na.ToString(CultureInfo.InvariantCulture) + (na == 1 ? " mod isn't allowed here." : " mods aren't allowed here.");
        // Live 2026-10-13: a refusal for the client's Core read "All ready" because no mod row failed.
        if (model.CoreRefused) return CoreRefusedTitle(model.Facts.CoreRequirements);
        int k = model.Required.Count - model.RequiredReady;
        if (model.Required.Count == 0) return head + (na == 0 ? " Nothing is required to join." : notAllowed);
        if (k == 0) return head + (na == 0 ? " All ready." : notAllowed);
        return head + " " + k.ToString(CultureInfo.InvariantCulture) + (k == 1 ? " needs attention." : " need attention.") + notAllowed;
    }

    private static string ServerCoreOf(IReadOnlyList<JoinCoreRequirement> requirements)
    {
        foreach (var r in requirements) if (r.ServerCore.Length > 0) return r.ServerCore;
        return "";
    }

    private static bool AnyKind(IReadOnlyList<JoinCoreRequirement> requirements, JoinCoreRequirementKind kind)
    {
        foreach (var r in requirements) if (r.Kind == kind) return true;
        return false;
    }

    /// <summary>The server's Progenitor version a Core refusal named, or empty.</summary>
    public static string CoreRefusedServerVersion(IReadOnlyList<JoinCoreRequirement>? requirements) =>
        requirements == null ? "" : ServerCoreOf(requirements);

    /// <summary>
    /// The refusal is about this client's Progenitor being too old (a capability only a newer one has), not about a server
    /// setting.
    /// </summary>
    public static bool CoreOutOfDate(IReadOnlyList<JoinCoreRequirement>? requirements) =>
        requirements != null && AnyKind(requirements, JoinCoreRequirementKind.Capability);

    /// <summary>Approved wording 2026-10-13: the headline of a refusal for this client's Progenitor.</summary>
    public const string OutOfDateTitle = "Your Progenitor is out of date";

    /// <summary>Approved wording 2026-10-13: the headline when the server's own Progenitor setting refuses every normal client.</summary>
    public const string ServerSetupTitle = "This server's setup isn't compatible with your game";

    /// <summary>The Update Progenitor button (the self-update, started from the join screens).</summary>
    public const string UpdateProgenitorButton = "Update Progenitor";

    /// <summary>The Core refusal's headline, or empty without Core requirements.</summary>
    public static string CoreRefusedTitle(IReadOnlyList<JoinCoreRequirement>? requirements)
    {
        if (requirements == null || requirements.Count == 0) return "";
        return CoreOutOfDate(requirements) ? OutOfDateTitle : ServerSetupTitle;
    }

    /// <summary>
    /// The Core refusal's body, in words a player can act on (approved 2026-10-13): <c>This server needs Progenitor
    /// 2026.10.195. You have 2026.10.191. Update Progenitor, then join again.</c>, or for the server's item-limit setting
    /// <c>Its admins need to fix a Progenitor setting before you can join. Let them know.</c> Feature names and the
    /// setting stay out of it (<see cref="CoreRefusedDetail"/> keeps them for the log and the readback). Empty without
    /// Core requirements.
    /// </summary>
    public static string CoreRefusedBody(IReadOnlyList<JoinCoreRequirement>? requirements, string? ownCore = null)
    {
        if (requirements == null || requirements.Count == 0) return "";
        if (!CoreOutOfDate(requirements)) return "Its admins need to fix a Progenitor setting before you can join. Let them know.";
        string server = ServerCoreOf(requirements);
        string own = (ownCore ?? "").Trim();
        return (server.Length > 0 ? "This server needs Progenitor " + server + "." : "This server needs a newer Progenitor.")
            + (own.Length > 0 ? " You have " + own + "." : "")
            + " Update Progenitor, then join again.";
    }

    /// <summary>
    /// The technical detail behind a Core refusal, for the log and the readback (never drawn): <c>missing catalog-v2
    /// equipment-stat synchronization; item limit 1024 (Networking.MaxSyncedPrefabsPerJoin)</c>. Empty without Core
    /// requirements.
    /// </summary>
    public static string CoreRefusedDetail(IReadOnlyList<JoinCoreRequirement>? requirements)
    {
        if (requirements == null) return "";
        var parts = new List<string>();
        foreach (var r in requirements)
        {
            string part = r.Kind == JoinCoreRequirementKind.Capability ? "missing " + r.Capability
                : "item limit " + (r.Limit > 0 ? r.Limit.ToString(CultureInfo.InvariantCulture) : "?") + " (Networking.MaxSyncedPrefabsPerJoin)";
            if (!parts.Contains(part)) parts.Add(part);
        }

        return string.Join("; ", parts);
    }

    /// <summary>What Update Progenitor says once the download started.</summary>
    public static string UpdateStarted(string version) =>
        "Downloading Progenitor " + version + ". It's installed when you're back at the title screen. Then join again.";

    /// <summary><c>4 of 6 ready</c>.</summary>
    public static string ReadyCount(JoinPageModel model) =>
        model == null ? "" : model.RequiredReady.ToString(CultureInfo.InvariantCulture) + " of "
            + model.Required.Count.ToString(CultureInfo.InvariantCulture) + " ready";

    /// <summary>The row's source line: the Thunderstore owner, or none.</summary>
    public static string SourceLine(JoinRequiredRow row)
    {
        if (row == null) return "";
        if (row.Origin == JoinRowOrigin.YouRequire) return "Your mod · this server doesn't run it";
        // C4: the Progenitor row says where its build comes from, or why it can't be switched here.
        if (row.Origin == JoinRowOrigin.Progenitor) return row.ProgenitorNote;
        if (row.LocalTest) return "Local test source · " + (row.Package ?? "") + " ([Install] LocalIndexPath; the server named none)";
        if (row.LocalTestNote.Length > 0 && !row.Ready && (row.Source & JoinRowSource.Thunderstore) == 0) return "No download source · " + row.LocalTestNote;
        if ((row.Source & JoinRowSource.Thunderstore) != 0) return "Thunderstore · " + Owner(row.Package);
        return row.Ready ? "" : "No download source";
    }

    /// <summary>The Thunderstore owner of <c>Owner-Name</c>.</summary>
    public static string Owner(string? package)
    {
        string p = (package ?? "").Trim();
        int dash = p.IndexOf('-');
        return dash > 0 ? p.Substring(0, dash) : p;
    }

    /// <summary><c>Needs 1.2.0 · You have nothing</c>, <c>You have 1.1.0</c>, <c>You have 1.3.0 (newer)</c>.</summary>
    public static string VersionLine(JoinRequiredRow row)
    {
        if (row == null) return "";
        if (row.Origin == JoinRowOrigin.YouRequire)
            return "You have " + (row.Have ?? "it") + " · the server has nothing";
        // JOINFLOW4: the kept-version lines.
        string keep = KeepLine(row);
        if (keep.Length > 0) return keep;
        string forThis = ForThisServerLine(row);
        if (forThis.Length > 0) return forThis;
        if (row.Action == JoinRowAction.UseKept && row.Kept != null)
            return "Needs " + NeedsWords(row) + " · You have " + (row.Have ?? "nothing") + " · " + row.Kept.PickVersion + " is kept, no download";
        // CLIENTPOLICY2: a required client mod states the admins' rule; C3/C4: a mod the server runs states the server's
        // match when it has one (exactly 1.2.0, 1.2.x, 1.2.0 or newer).
        string needs = "Needs " + NeedsWords(row);
        // C4: the Progenitor row; the same release number can be another build.
        if (row.Origin == JoinRowOrigin.Progenitor)
            return needs + " · You have " + (row.Ready ? row.Have ?? row.RequiredVersion
                : row.ProgenitorSameVersion ? "another build of " + row.RequiredVersion
                : row.Have ?? "another build");
        // JOINTURNON: the player's own copy is there, switched off: say so instead of "You have nothing".
        if (row.OffCopy != null && row.Have == null) return needs + " · You have " + OffCopyWords(row.OffCopy);
        string have = row.Have == null ? "nothing" : row.Status == JoinRowStatus.Newer ? row.Have + " (newer)" : row.Have;
        return needs + " · You have " + have;
    }

    /// <summary>
    /// What a row needs, as a line part after <c>Needs</c>: the admins' rule of a required client mod (CLIENTPOLICY2) or the
    /// server's match for a mod it runs (C3/C4 wording): <c>exactly 1.2.0</c>, <c>1.2.x</c> (Same minor), <c>1.2.0 or newer</c>,
    /// <c>any version</c>; the plain version when the server judges by ranges or did not say.
    /// </summary>
    public static string NeedsWords(JoinRequiredRow row)
    {
        if (row == null) return "";
        var rule = row.PolicyRule ?? row.MatchRule;
        if (rule == null) return row.RequiredVersion.Length > 0 ? row.RequiredVersion : "any version";
        return NeedsPhrase(rule.Value, row.RequiredVersion);
    }

    /// <summary>
    /// A rule on a version in the join screens' words: <c>exactly 1.2.0</c>, <c>1.2.x</c>, <c>1.2.0 or newer</c>,
    /// <c>any version</c> (also for an empty version).
    /// </summary>
    public static string NeedsPhrase(ClientModRule rule, string? version)
    {
        string v = (version ?? "").Trim();
        if (rule == ClientModRule.Any || v.Length == 0) return "any version";
        return rule switch
        {
            ClientModRule.Exact => "exactly " + v,
            ClientModRule.Minor => (CompatVersion.MinorOf(v) ?? v) + ".x",
            _ => v + " or newer",
        };
    }

    /// <summary>The readback name of a row's origin: <c>server-requires</c>, <c>you-require</c>, <c>required-client</c>.</summary>
    public static string OriginName(JoinRowOrigin origin) => origin switch
    {
        JoinRowOrigin.YouRequire => "you-require",
        JoinRowOrigin.ClientPolicy => "required-client",
        JoinRowOrigin.Progenitor => "progenitor",
        _ => "server-requires",
    };

    /// <summary>The row's tags as words: <c>you're ahead</c>, <c>applies now</c>, <c>needs restart</c>.</summary>
    public static IReadOnlyList<string> TagWords(JoinRowTags tags)
    {
        var list = new List<string>(3);
        if ((tags & JoinRowTags.Ahead) != 0) list.Add("you're ahead");
        if ((tags & JoinRowTags.AppliesNow) != 0) list.Add("applies now");
        if ((tags & JoinRowTags.NeedsRestart) != 0) list.Add("needs restart");
        return list;
    }

    /// <summary>The action's caption.</summary>
    public static string ActionCaption(JoinRowAction action) => action switch
    {
        JoinRowAction.Install => "Install",
        JoinRowAction.Update => "Update",
        JoinRowAction.AskAdmin => "Ask the admin",
        JoinRowAction.TellAdmin => "Tell the admin",
        JoinRowAction.TurnOff => "Turn off",
        JoinRowAction.UseVersion => "Use version",
        JoinRowAction.UseKept => "Use kept",
        JoinRowAction.TurnOn => TurnOnButton,
        JoinRowAction.UseProgenitor => "Use Progenitor",
        _ => ReadyWord,
    };

    // ---- JOINTURNON: installed but turned off ------------------------------------------------------

    public const string TurnOnButton = "Turn on";
    public const string TurnedOffWord = "Turned off";
    public const string TurnOnNotAvailable = "Turning mods on is not available here. Turn it on in Mods, then join again.";

    /// <summary><c>1.13.22 (turned off)</c>, <c>1.13.22 (turns on at next start)</c>; <c>a copy</c> when its version is unknown.</summary>
    public static string OffCopyWords(JoinOffCopy off) =>
        (off == null || off.Version.Length == 0 ? "a copy" : off.Version)
        + (off != null && off.TurnsOnAtNextStart ? " (turns on at next start)" : " (turned off)");

    /// <summary>The status word a turned-off row leads with (the panel's warning tone).</summary>
    public static string StatusWordOf(JoinRequiredRow row) =>
        row?.Status == JoinRowStatus.TurnedOff ? TurnedOffWord : "";

    /// <summary>A turned-off row's short version: <c>you have 1.13.22 (turned off)</c>.</summary>
    public static string CompactOffVersion(JoinRequiredRow row) =>
        row?.OffCopy == null ? "" : "you have " + OffCopyWords(row.OffCopy);

    /// <summary>Live 2026-10-05: a recommended copy held by the per-server pause.</summary>
    public const string PausedHereWord = "Paused here";

    /// <summary>A recommended copy switched on that loads at the next start.</summary>
    public const string OnAtRestartWord = "On at restart";

    /// <summary>
    /// A recommended row's state where <c>Installed</c> goes: <c>Installed</c>, <c>Turned off</c> (installed; its switch
    /// turns it on, the required rows' status word), <c>On at restart</c>, <c>Paused here</c> (installed and on, held by
    /// the per-server pause), or empty (not installed). Live 2026-10-05: <c>Installed · turned off</c> was cut to
    /// <c>Installed ·…</c> in the cell, so each state is one short word pair (<see cref="RecommendedWordWidth"/>).
    /// </summary>
    public static string RecommendedWord(JoinRecommendedRow row)
    {
        if (row == null) return "";
        if (row.TurnedOff) return TurnedOffWord;
        if (row.OffCopy is { TurnsOnAtNextStart: true }) return OnAtRestartWord;
        if (row.OffCopy is { PausedHere: true }) return PausedHereWord;
        return row.Installed ? InstalledWord : "";
    }

    /// <summary>Every word <see cref="RecommendedWord"/> can draw.</summary>
    public static readonly IReadOnlyList<string> RecommendedWords = new[] { InstalledWord, TurnedOffWord, OnAtRestartWord, PausedHereWord };

    /// <summary>The recommended row's state cell: wide enough for its longest word at <paramref name="fontSize"/> (at least 110 px).</summary>
    public static float RecommendedWordWidth(float fontSize)
    {
        float w = 0f;
        foreach (string word in RecommendedWords)
            w = Math.Max(w, Progenitor.Ui.CellLayout.EstimateTextWidth(word, fontSize));
        return Math.Max(110f, (float)Math.Ceiling(w) + 16f);
    }

    /// <summary>Live 2026-10-05: <c>Alpha is turned on. This server doesn't run it, so it stays paused here until you leave.</c></summary>
    public static string TurnedOnPausedHere(string name) =>
        name + " is turned on. This server doesn't run it, so it stays paused here until you leave.";

    /// <summary><c>Alpha is turned on. It loads when you leave this server.</c> (the joined server holds other mods paused)</summary>
    public static string TurnedOnAfterLeave(string name) => name + " is turned on. It loads when you leave this server.";

    /// <summary>
    /// The mods page's bottom button: <c>Install N mods</c> when it only downloads, <c>Apply N changes</c> (N = installs +
    /// turn-ons) once a checked row turns a mod on. Empty when there is nothing to do.
    /// </summary>
    public static string BottomButton(int installs, int turnOns)
    {
        if (turnOns <= 0) return installs > 0 ? InstallButton(installs) : "";
        int n = installs + turnOns;
        return "Apply " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " change" : " changes");
    }

    /// <summary><c>Alpha is turned on and loaded.</c></summary>
    public static string TurnedOnNow(string name) => name + " is turned on and loaded.";

    /// <summary><c>Alpha is turned on; it loads in a moment.</c> (turned on, not yet declared)</summary>
    public static string TurnedOnLoading(string name) => name + " is turned on; it loads in a moment.";

    /// <summary>Inside an install window: <c>Alpha is turned on; the join continues by itself once it loads.</c></summary>
    public static string TurnedOnWindow(string name) => name + " is turned on; the join continues by itself once it loads.";

    /// <summary>On a refused join, once it loaded: <c>Alpha is turned on. Join now; no download or restart needed.</c></summary>
    public static string TurnedOnRefused(string name) => name + " is turned on. Join now; no download or restart needed.";

    /// <summary><c>Alpha turns on at the next start of V Rising.</c></summary>
    public static string TurnsOnAtNextStart(string name) => name + " turns on at the next start of V Rising.";

    /// <summary><c>Can't turn on Alpha: &lt;why&gt;</c></summary>
    public static string TurnOnFailed(string name, string why) =>
        "Can't turn on " + name + ": " + (string.IsNullOrWhiteSpace(why) ? "the switch refused." : why.Trim());

    /// <summary>The confirmation's facts for the turn-ons pressed with installs: <c>Turns on · Alpha 1.2 (now)</c>.</summary>
    public static List<Progenitor.Ui.DialogFact> TurnOnFacts(JoinThunderstoreFacts t)
    {
        var facts = new List<Progenitor.Ui.DialogFact>();
        bool first = true;
        foreach (var r in t?.TurnOns ?? Array.Empty<JoinTurnOnTarget>())
        {
            facts.Add(new(first ? "Turns on" : "", r.Name + (r.Version.Length > 0 ? " " + r.Version : "") + " · already installed, no download"
                + (r.AppliesNow && !r.TurnsOnAtNextStart ? "" : " · at the next start")));
            first = false;
        }

        return facts;
    }

    /// <summary>JOINFLOW2: a row's caption; "Use version X" names the server's version; JOINFLOW4: "Use kept X".</summary>
    public static string ActionCaption(JoinRequiredRow row)
    {
        if (row == null) return ActionCaption(JoinRowAction.Ready);
        // JOINFLOW4: the caption says when the restart route is what will happen.
        bool restartKeep = row.Status == JoinRowStatus.Newer && row.Kept is { CanKeepActive: true, KeepNow: false };
        if (row.Action == JoinRowAction.UseVersion && row.RequiredVersion.Length > 0)
            return "Use version " + row.RequiredVersion + (restartKeep ? " (restart)" : "");
        if (row.Action == JoinRowAction.UseKept && row.Kept != null)
            return "Use kept " + row.Kept.PickVersion + (row.Kept.PickRoute == "restart" ? " (restart)" : "");
        // C4: the server's Progenitor build, by its release number.
        if (row.Action == JoinRowAction.UseProgenitor) return ProgenitorExact.UseCaption(row.RequiredVersion);
        // LOCALJOIN1: a local test source says so on its button.
        if (row.LocalTest && row.Action == JoinRowAction.Install) return Install.LocalTestSource.InstallCaption;
        if (row.LocalTest && row.Action == JoinRowAction.Update) return Install.LocalTestSource.UpdateCaption;
        return ActionCaption(row.Action);
    }

    /// <summary>The readback name of an action.</summary>
    public static string ActionName(JoinRowAction action) => action switch
    {
        JoinRowAction.Install => "install",
        JoinRowAction.Update => "update",
        JoinRowAction.AskAdmin => "ask-admin",
        JoinRowAction.TellAdmin => "tell-admin",
        JoinRowAction.TurnOff => "turn-off",
        JoinRowAction.UseVersion => "use-version",
        JoinRowAction.UseKept => "use-kept",
        JoinRowAction.TurnOn => "turn-on",
        JoinRowAction.UseProgenitor => "use-progenitor",
        _ => "ready",
    };

    // ---- JOINFLOW2: Thunderstore installs, Join now, restart and rejoin ------------------------

    public const string InstallCanceled = "canceled";
    public const string TsTrustTitle = "Install from Thunderstore?";
    public const string TsInstallButton = "Install";

    /// <summary>
    /// The Thunderstore confirmation's key-change line (2026-10-05): the catalog for this address is signed by another key
    /// than the one pinned. <c>This server's key changed since Oct 4, 2026; it was 00b9749a0441bdc5.</c> Empty when there
    /// is no previous key.
    /// </summary>
    public static string KeyChangedLine(string? previousFingerprint, string? previousSeen)
    {
        string fp = (previousFingerprint ?? "").Trim();
        if (fp.Length == 0) return "";
        string seen = (previousSeen ?? "").Trim();
        return "This server's key changed" + (seen.Length > 0 ? " since " + seen : "") + "; it was " + fp + ".";
    }
    public const string NothingInstalled = "Nothing was installed.";
    public const string BatchBusy = "Installs are still running. Wait for them to finish, then press it again.";
    public const string LaterButton = "Later";
    public const string JoinNowButton = "Join now";
    public const string RestartRejoinButton = "Restart and rejoin";
    public const string CancelButton = "Cancel";
    public const string RestartTitle = "Restart needed";
    public const string RestartBody = "Some mods load only when V Rising starts. Restart now and we'll reconnect you to this server.";
    public const string RestartManual = "V Rising will close. Start it again and Progenitor will reconnect you.";
    public const string RestartHosted = "Some mods load only when V Rising starts. Restart V Rising, then start your private game again.";
    public const string RestartNoAddress = "Some mods load only when V Rising starts. Restart V Rising, then join this server again.";
    public const string JoinNowTitle = "Ready to join";
    public const string JoinNowBody = "Everything this server needs is installed and loaded. Join it now; no restart needed.";
    public const string InWorldLoadedNote = "Installed and loaded. No restart needed.";
    public const string WindowLoadedNote = "Installed and loaded; the join continues by itself.";

    /// <summary>A6: the window's batch loaded; the re-answer has not gone out yet (it waits for the mods to declare).</summary>
    public const string WindowTellingNote = "Installed and loaded; telling the server…";

    public const string JoinAgainButton = "Join again";

    /// <summary>F22: the Result page's button while the server restarts.</summary>
    public const string RejoinWhenBackButton = "Rejoin when it's back";

    /// <summary>F22, F31: the Result page's note while the server restarts: <c>Why: Stop server to apply.</c> (empty without a reason).</summary>
    public static string ServerRestarting(string server, string why) =>
        string.IsNullOrWhiteSpace(why) ? "" : "Why: " + why.Trim().TrimEnd('.') + ".";

    /// <summary>F31 (live 2026-10-08): the Result page's title while the server restarts: <c>Night Garden is restarting</c>.</summary>
    public static string RestartingTitle(string server) =>
        (string.IsNullOrWhiteSpace(server) ? "The server" : server) + " is restarting";

    /// <summary>F31: the Result page's body while the server restarts (not "Ready to join").</summary>
    public const string RestartingBody = "Progenitor rejoins it when it's back (up to 5 minutes).";

    /// <summary>F22: the Joining page between attempts.</summary>
    public static string WaitingForServer(string server, int attempt) =>
        "Waiting for " + (string.IsNullOrWhiteSpace(server) ? "the server" : server) + " to come back (attempt "
        + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); trying again in a few seconds.";

    /// <summary>F19: a rejoin that did not join: <c>Couldn't rejoin Night Garden: &lt;reason&gt;.</c></summary>
    public static string RejoinFailed(string server, string reason) =>
        "Couldn't rejoin " + (string.IsNullOrWhiteSpace(server) ? "the server" : server) + ": " + (reason ?? "").Trim().TrimEnd('.') + ".";

    public const string PromptClosedReason = "the game's password prompt closed without joining";

    /// <summary>A6: the reason when the connection ended with no refusal reaching this client.</summary>
    public const string ConnectionEndedReason = "the connection ended";

    /// <summary>A6: the window's re-answer did not go out: the server was not told, so its window will end the join.</summary>
    public static string WindowNotTold(string server, string names) =>
        (string.IsNullOrWhiteSpace(names) ? "The mods are" : names + (names.IndexOf(',') >= 0 ? " are" : " is")) + " installed and loaded, but "
        + (string.IsNullOrWhiteSpace(server) ? "the server" : server) + " could not be told on this connection, so its install window will end this join. "
        + "Join again: the fix is in place now.";

    /// <summary>
    /// A6: the join an install window held ended (the server's refusal, or the connection): its reason, and whether the
    /// fix is in place (then Join again finishes it).
    /// </summary>
    public static string WindowEnded(string server, string reason, bool fixedNow)
    {
        string who = string.IsNullOrWhiteSpace(server) ? "The server" : server;
        string why = (reason ?? "").Trim();
        string head = who + " ended this join" + (why.Length > 0 ? ": " + why.TrimEnd('.') + "." : ".");
        return fixedNow ? head + " Everything it requires is in place now, so joining again finishes it." : head;
    }
    /// <summary>INPLACE1: the Result page's title and button when the fix finished in the world.</summary>
    public const string InPlaceTitle = "Installed";
    public const string DoneButton = "Done";

    /// <summary>INPLACE1: the in-place confirmation: the server was told on this connection; no rejoin, no restart.</summary>
    public static string SentInPlace(string server, string names, string done = "installed") =>
        "Sent to " + (string.IsNullOrWhiteSpace(server) ? "the server" : server) + ": "
        + (string.IsNullOrWhiteSpace(names) ? "the mods are" : names + (names.IndexOf(',') >= 0 ? " are" : " is"))
        + " " + done + " now. You stay where you are; no rejoin or restart needed.";

    /// <summary>INPLACE1: the in-place answer could not go out on this connection (the server re-checks on its own).</summary>
    public static string InPlaceNotSent(string server, string names, string done = "installed and loaded") =>
        (string.IsNullOrWhiteSpace(names) ? "The mods are" : names + (names.IndexOf(',') >= 0 ? " are" : " is")) + " " + done + ", but "
        + (string.IsNullOrWhiteSpace(server) ? "the server" : server) + " could not be told on this connection, so its warning may stay until it checks your mods again. You stay where you are.";

    /// <summary>
    /// INPLACE1 review: the fix is in, but the server's check would still fail (or cannot be confirmed), so this client did
    /// not answer again: a failing answer would disconnect the player. Says what still mismatches; never "you stay".
    /// </summary>
    public static string InPlaceNotYet(string server, string names, string done, IReadOnlyList<string> problems, bool checkable)
    {
        string who = string.IsNullOrWhiteSpace(server) ? "the server" : server;
        string what = (string.IsNullOrWhiteSpace(names) ? "The mods are" : names + (names.IndexOf(',') >= 0 ? " are" : " is")) + " " + done + ", but ";
        if (!checkable)
            return what + "this client can't confirm its mods now match what " + who + " checks, so it did not answer " + who
                + " again (a failing answer would disconnect you). " + Capital(who) + " sees the change when you next join.";
        var shown = new List<string>();
        foreach (string p in problems ?? Array.Empty<string>()) { if (shown.Count == 3) break; if (!string.IsNullOrWhiteSpace(p)) shown.Add(p.Trim().TrimEnd('.')); }
        int more = (problems?.Count ?? 0) - shown.Count;
        return what + who + " would still refuse this client's mods: " + string.Join("; ", shown) + (more > 0 ? "; and " + more.ToString(CultureInfo.InvariantCulture) + " more" : "")
            + ". It was not answered again, since a failing answer would disconnect you. Fix the rest from the join window or Install Mods.";
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>INPLACE1: the names of the rows that loaded, in order, joined with commas.</summary>
    public static string LoadedNames(IReadOnlyList<JoinInstallRowState> rows)
    {
        var names = new List<string>();
        if (rows != null)
            foreach (var r in rows)
                if (r != null && r.Phase == JoinInstallPhase.Loaded && r.Request.Name.Length > 0) names.Add(r.Request.Name);
        return string.Join(", ", names);
    }
    public const string SummaryStillMissing = "Still missing";
    public const string SummaryServerMustUpdate = "Server must update";

    /// <summary>JOINTURNON: the after-restart summary's word for a required mod whose copy is still turned off.</summary>
    public const string SummaryTurnedOff = "Still turned off";
    /// <summary>SERVEREMPTY1: the Joining page's last line when the game's own password prompt took over.</summary>
    public const string PasswordPromptNote = "This server needs its password. Enter it in the game's prompt.";

    /// <summary>JOINPW1: the Server tab Join connected without a password and the server refused it for its password.</summary>
    public const string PasswordRefusedNote = "The server asked for a password. Opening the game's password prompt.";

    public const string RejoinPasswordNote =
        "This server needs its password, which Progenitor never saves. Join it again from the server list and enter the password there.";

    /// <summary><c>Install 3 mods from Thunderstore that Night Garden lists?</c></summary>
    public static string TsTrustIntro(string server, int mods) =>
        "Install " + Count(mods, "mod", "mods") + " from Thunderstore that " + (string.IsNullOrWhiteSpace(server) ? "this server" : server.Trim()) + " lists?";

    /// <summary>A Thunderstore row's state where its button was: <c>Downloading 62%</c>, <c>Loaded</c>, <c>After restart</c>…</summary>
    public static string InstallPhaseWord(JoinInstallRowState state)
    {
        if (state == null) return "";
        return state.Phase switch
        {
            JoinInstallPhase.Queued => "Queued",
            JoinInstallPhase.Resolving => "Checking…",
            JoinInstallPhase.Downloading => state.Percent >= 0
                ? "Downloading " + state.Percent.ToString(CultureInfo.InvariantCulture) + "%"
                : "Downloading…",
            JoinInstallPhase.Installing => "Installing…",
            JoinInstallPhase.Loaded => "Loaded",
            JoinInstallPhase.AfterRestart => "After restart",
            JoinInstallPhase.Refused => "Refused",
            _ => "Failed",
        };
    }

    /// <summary>
    /// The tags once an install ended: the catalog's prediction (applies now / needs restart) gives way to what
    /// the install did — <c>applies now</c> when it loaded, <c>needs restart</c> when it waits for the next start.
    /// </summary>
    public static JoinRowTags TagsAfter(JoinRowTags tags, JoinInstallRowState? state)
    {
        if (state == null) return tags;
        if (state.Phase == JoinInstallPhase.Loaded) return (tags & ~JoinRowTags.NeedsRestart) | JoinRowTags.AppliesNow;
        if (state.Phase == JoinInstallPhase.AfterRestart) return (tags & ~JoinRowTags.AppliesNow) | JoinRowTags.NeedsRestart;
        return tags;
    }

    /// <summary>The readback name of a phase.</summary>
    public static string InstallPhaseName(JoinInstallPhase phase) => phase switch
    {
        JoinInstallPhase.Queued => "queued",
        JoinInstallPhase.Resolving => "resolving",
        JoinInstallPhase.Downloading => "downloading",
        JoinInstallPhase.Installing => "installing",
        JoinInstallPhase.Loaded => "loaded",
        JoinInstallPhase.AfterRestart => "after-restart",
        JoinInstallPhase.Refused => "refused",
        _ => "failed",
    };

    /// <summary>The sentence under a finished batch.</summary>
    public static string BatchSummary(IReadOnlyList<JoinInstallRowState> rows)
    {
        if (rows == null || rows.Count == 0) return "";
        int loaded = 0, later = 0;
        var failed = new List<string>();
        foreach (var r in rows)
        {
            if (r.Phase == JoinInstallPhase.Loaded) loaded++;
            else if (r.Phase == JoinInstallPhase.AfterRestart) later++;
            else if (r.Terminal) failed.Add(r.Request.Name + " (" + (r.Reason.Length > 0 ? r.Reason : InstallPhaseWord(r).ToLowerInvariant()) + ")");
        }

        var parts = new List<string>(3);
        if (loaded > 0) parts.Add("Loaded " + Count(loaded, "mod", "mods") + ".");
        if (later > 0) parts.Add(Count(later, "mod loads", "mods load") + " after a restart.");
        if (failed.Count > 0) parts.Add("Not installed: " + string.Join("; ", failed) + ".");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// JOINFLOW4: the "Use version X" sentence of the confirmation: the newer copy is kept for other servers, or
    /// (when it cannot be kept: another tool's copy, a plugins copy) replaced, with the reason.
    /// </summary>
    public static string UseVersionWarning(JoinInstallRequest r) =>
        r == null ? "" : r.Keeps
            ? (r.KeepsNow ? KeepSentence(r.Name, r.Replaces, r.Version)
                : r.Name + ": " + r.Version + " for this server after a restart; your " + (string.IsNullOrEmpty(r.Replaces) ? "newer copy" : r.Replaces)
                  + " is kept for other servers.")
            : UseVersionWarning(r.Name, r.Replaces, r.Version) + (r.KeepReason.Length > 0 ? " " + r.KeepReason : "");

    /// <summary>The replacing form: the newer copy cannot be kept beside the server's version.</summary>
    public static string UseVersionWarning(string name, string have, string version) =>
        name + ": your newer " + (string.IsNullOrEmpty(have) ? "copy" : have) + " is replaced by " + version + ".";

    /// <summary>JOINFLOW4: <c>Alpha: 1.8.2 for this server; your 1.9.0 is kept for other servers.</c></summary>
    public static string KeepSentence(string name, string have, string version) =>
        name + ": " + version + " for this server; your " + (string.IsNullOrEmpty(have) ? "newer copy" : have) + " is kept for other servers.";

    /// <summary>
    /// JOINFLOW4: the line of a row that is ahead and offers the server's version while the newer copy can be kept:
    /// <c>Use server's 1.8.2 · your 1.9.0 is kept for other servers</c>. Empty otherwise.
    /// </summary>
    public static string KeepLine(JoinRequiredRow row)
    {
        if (row == null || row.Status != JoinRowStatus.Newer || row.Have == null) return "";
        if (row.Action != JoinRowAction.UseVersion) return "";
        if (row.Kept == null || !row.Kept.CanKeepActive) return "";
        // Live 2026-10-04: never promise a live keep that cannot happen (a copy the reloader cannot unload now).
        return row.Kept.KeepNow
            ? "Use server's " + row.RequiredVersion + " · your " + row.Have + " is kept for other servers"
            : "Use server's " + row.RequiredVersion + " after a restart · your " + row.Have + " is kept";
    }

    /// <summary>
    /// JOINFLOW4 (live 2026-10-04): why a row takes the restart route or cannot keep the player's copy, as
    /// <c>restart: &lt;reason&gt;</c> or <c>can't keep yours: &lt;reason&gt;</c>. Empty when nothing needs saying.
    /// </summary>
    public static string KeepReasonLine(JoinRequiredRow row)
    {
        var k = row?.Kept;
        if (row == null || k == null) return "";
        if (row.Action == JoinRowAction.UseKept)
            return k.PickRoute == "restart" && k.PickReason.Length > 0 ? "restart: " + k.PickReason : "";
        if (row.Status != JoinRowStatus.Newer || row.Action != JoinRowAction.UseVersion) return "";
        if (k.KeepReason.Length == 0) return "";
        if (!k.CanKeepActive) return "can't keep yours: " + k.KeepReason;
        return k.KeepNow ? "" : "restart: " + k.KeepReason;
    }

    /// <summary>
    /// JOINFLOW4: a ready row whose active copy was placed for a server while the player's own is kept:
    /// <c>1.8.2 for this server · 1.9.0 kept</c>. Empty otherwise.
    /// </summary>
    public static string ForThisServerLine(JoinRequiredRow row)
    {
        var k = row?.Kept;
        if (row == null || !row.Ready || k == null || k.ActiveForServer.Length == 0 || row.Have == null) return "";
        string mine = "";
        foreach (var v in k.Kept)
            if (string.Equals(v, k.Preferred, StringComparison.OrdinalIgnoreCase) && !string.Equals(v, row.Have, StringComparison.OrdinalIgnoreCase)) mine = v;
        if (mine.Length == 0)
            foreach (var v in k.Kept)
                if (!string.Equals(v, row.Have, StringComparison.OrdinalIgnoreCase)) { mine = v; break; }
        return mine.Length == 0 ? "" : row.Have + " for this server · " + mine + " kept";
    }

    /// <summary>The answer for a Thunderstore row whose exact version is gone.</summary>
    public static string VersionGoneAnswer(JoinRequiredRow row) =>
        row.Name + " " + row.RequiredVersion + (row.Availability == Install.PackageVersionAvailability.NotListed
            ? " is not on Thunderstore's V Rising list."
            : " is no longer on Thunderstore" + (row.ThunderstoreLatest.Length > 0 ? " (it has " + row.ThunderstoreLatest + ")." : "."))
        + " Ask the admins to update the server.";

    /// <summary>
    /// Live 2026-10-05: the game's connect failure (<c>ConnectionStatusChangeReason</c>, by name) in words for the
    /// Joining page: <c>Could not reconnect: Connection timed out. Join the server again from the server list.</c>.
    /// An unknown name is spelled out (<c>FooBar</c> → <c>Foo bar</c>).
    /// </summary>
    public static string ConnectFailureWords(string? reasonName) => (reasonName ?? "").Trim() switch
    {
        "Timeout" => "Connection timed out",
        "FailedToEstablishConnection" => "Could not reach the server",
        "ServerFull" => "The server is full",
        "NoFreeSlots" or "NoNewUserSlotsFree" => "The server has no free slots",
        "IncorrectPassword" => "Wrong password",
        "IncorrectVersion" => "The server runs another game version",
        "Banned" => "You are banned from this server",
        "BlockedUserOnServer" => "You are blocked on this server",
        "Kicked" => "The server disconnected you",
        "ServerShutdown" => "The server shut down",
        "ServerStartingUp" => "The server is still starting up",
        "ServerError" => "The server had an error",
        "AuthenticationError" or "AuthSessionCancelled" => "Authentication failed",
        "MultiplayerNoLongerAvailable" => "Multiplayer is not available",
        "ClientError" => "The game had an error",
        "" or "Unknown" => "The connection failed",
        var other => Spelled(other),
    };

    private static string Spelled(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c)) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary><c>Connecting to 1.2.3.4:9876…</c></summary>
    public static string ConnectingTo(string endpoint) => "Connecting to " + endpoint + "…";

    /// <summary><c>Rejoining Night Garden</c> / <c>Joining Night Garden</c></summary>
    public static string RejoinTitle(string server, bool afterRestart) =>
        (afterRestart ? "Rejoining " : "Joining ") + (string.IsNullOrWhiteSpace(server) ? "this server" : server.Trim());

    /// <summary>The Rejoining page's body while it connects after a restart (the status line says where, with its percentage).</summary>
    public const string RejoinRestartedLine = "V Rising restarted to finish installing.";

    public static string RejoinBody(string endpoint, bool afterRestart) =>
        (afterRestart ? RejoinRestartedLine + " " : "") + ConnectingTo(endpoint);

    /// <summary><c>Required (2 of 3 ready)</c></summary>
    public static string SummaryRequiredHeading(int ready, int total) =>
        "Required (" + ready.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " ready)";

    /// <summary><c>Recommended (1 added)</c></summary>
    public static string SummaryRecommendedHeading(int added) =>
        "Recommended (" + added.ToString(CultureInfo.InvariantCulture) + " added)";

    public static string StatusName(JoinRowStatus status) => status switch
    {
        JoinRowStatus.Missing => "missing",
        JoinRowStatus.Older => "older",
        JoinRowStatus.Newer => "newer",
        JoinRowStatus.ServerLacks => "server-lacks",
        JoinRowStatus.TurnedOff => "turned-off",
        _ => "ready",
    };

    /// <summary>The footer under the lists naming what cannot be fixed here, or empty.</summary>
    public static string CannotFixFooter(JoinPageModel model)
    {
        if (model == null) return "";
        var noSource = new List<string>();
        var ahead = new List<string>();
        var gone = new List<string>();
        string progenitor = "";
        foreach (var r in model.Required)
        {
            // C4: the Progenitor row says its own reason (a development build, a mod manager, not on Thunderstore here).
            if (r.Origin == JoinRowOrigin.Progenitor)
            {
                if (r.Action == JoinRowAction.TellAdmin)
                    progenitor = "Progenitor: this server requires exactly " + r.RequiredVersion
                        + (r.ProgenitorNote.Length > 0 ? ", and " + r.ProgenitorNote : "") + ". Ask its admins.";
                continue;
            }

            if (r.Action == JoinRowAction.AskAdmin && r.VersionGone) gone.Add(r.Name + " " + r.RequiredVersion);
            else if (r.Action == JoinRowAction.AskAdmin) noSource.Add(r.Name);
            else if (r.Action == JoinRowAction.TellAdmin) ahead.Add(r.Name);
        }

        var parts = new List<string>(4);
        if (progenitor.Length > 0) parts.Add(progenitor);
        // JOINFLOW2: the exact version a server needs that Thunderstore no longer serves.
        if (gone.Count > 0)
            parts.Add(string.Join(", ", gone) + ": " + (gone.Count == 1 ? "that version is" : "those versions are")
                + " no longer on Thunderstore. Ask the admins to update the server.");
        // Live 2026-10-04: a missing mod is not something only the admins can change (the player could find
        // it), so each case says what is actually missing.
        if (noSource.Count > 0)
            parts.Add(string.Join(", ", noSource) + ": this server didn't say where to get "
                + (noSource.Count == 1 ? "it" : "them") + ". Ask its admins.");
        if (ahead.Count > 0)
            parts.Add(string.Join(", ", ahead) + ": you have a newer version than the server; its admins must update "
                + (ahead.Count == 1 ? "it" : "them") + ".");
        return string.Join(" ", parts);
    }

    /// <summary><c>Install 3 mods</c>.</summary>
    public static string InstallButton(int n) =>
        "Install " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " mod" : " mods");

    /// <summary>The short message "Ask the admin" puts on the clipboard.</summary>
    public static string AskAdminMessage(JoinRequiredRow row, string server) =>
        "Hi! Joining " + server + " needs " + row.Name + " (" + row.Id + " "
        + (row.PolicyRule != null || row.MatchRule != null ? NeedsWords(row) : Version(row.RequiredVersion))
        + "), but I can't find a download for it. Could you share where to get it?";

    /// <summary>The short message "Tell the admin" puts on the clipboard.</summary>
    public static string TellAdminMessage(JoinRequiredRow row, string server) =>
        // C4: the server holds players to its own Progenitor build and this client can't switch to it here.
        row.Origin == JoinRowOrigin.Progenitor
            ? ProgenitorExact.TellAdminMessage(server, row.RequiredVersion, row.ProgenitorSameVersion ? "another build of " + row.RequiredVersion : row.Have, row.ProgenitorNote)
        : row.Origin == JoinRowOrigin.ClientPolicy
            // CLIENTPOLICY2: the admins' own rule refuses a newer copy; they can change the rule.
            ? "Hi! " + server + " requires " + row.Name + " (" + NeedsWords(row) + "), but I have " + (row.Have ?? "a newer version")
              + " (" + row.Id + "). Could the rule allow newer versions?"
            : "Hi! " + server + " runs " + row.Name + " " + Version(row.RequiredVersion) + ", but I have " + (row.Have ?? "a newer version")
              + " (" + row.Id + "). Could the server update it?";

    /// <summary>The answer to Turn off.</summary>
    public static string TurnOffAnswer(JoinRequiredRow row) =>
        row.Name + " needs servers that run it too, and this one doesn't. Turn it off on its page in Mods, then join again.";

    private static string Version(string v) => string.IsNullOrEmpty(v) ? "(any version)" : v;
}
