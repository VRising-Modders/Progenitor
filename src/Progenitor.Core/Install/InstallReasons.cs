using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Install;

/// <summary>
/// Every refusal and decision reason this pipeline can produce. Game-free constants so
/// tests assert the exact sentence the UI will show.
/// </summary>
internal static class InstallReasons
{
    public const string IndexHostKey = "index";

    public const string SourceThunderstore = "thunderstore";
    public const string SourceLabelThunderstore = "Thunderstore";

    public const string MalformedJson = "the package index is not valid JSON";
    public const string IndexNotArray = "the package index must be a JSON array of packages";
    public const string IndexTruncated = "the package index JSON is truncated or incomplete";
    public const string IndexEmpty = "the package index is empty";
    public const string IndexTooLarge = "the package index exceeds the entry cap";
    public const string IndexDisabled = "the Thunderstore source is disabled";
    public const string MissingListingIdentity = "a package is missing name or version and was skipped";

    public const string FetchSizeCeiling = "the download exceeded the size ceiling";
    public const string FetchTimeout = "the download timed out";

    /// <summary>REMOTEINST1: fewer bytes arrived than the server announced.</summary>
    public const string FetchTruncated = "the download ended before all of it arrived";
    public const string FetchCancelled = "the download was canceled";
    public const string FetchRedirectBound = "the redirect chain exceeded the bound";
    public const string FetchRedirectInsecure = "a redirect to a non-HTTPS URL was refused";
    public const string FetchRedirectHost = "a redirect to a host this download may not use was refused";
    public const string FetchTooManyRetries = "the server kept returning an error; stopped retrying to avoid a flood";
    public const string FetchRateLimited = "the server returned 429 Too Many Requests";
    public const string FetchServerError = "the server returned a 5xx error";
    public const string FetchHttpError = "the download was refused by the server";
    public const string FetchNotHttps = "only HTTPS URLs are fetched";
    public const string FetchNoUrl = "this listing has no download URL";
    public const string FetchTransport = "the download failed";

    public const string DocumentIdentityMissing =
        "this listing has no owner, package name, or version, so its README cannot be fetched";
    public const string DocumentMalformedJson = "the listing document is not valid JSON";
    public const string DocumentMissingMarkdown = "the listing document did not include markdown";
    public const string DocumentNotObject = "the listing document is not a JSON object";

    public const string InspectTraversal = "a zip entry path contains '..' and was refused";
    public const string InspectAbsolute = "a zip entry has an absolute or drive-qualified path and was refused";
    public const string InspectRooted = "a zip entry is rooted and was refused";
    public const string InspectSymlink = "a zip entry is a symlink or escaping link and was refused";
    public const string InspectDuplicate = "two zip entries would land on the same destination and were refused";
    public const string InspectExpansion = "the zip's uncompressed size or expansion ratio exceeds the ceiling";
    public const string InspectReserved = "a zip entry would land on a reserved Progenitor image name and was refused";
    public const string InspectHashMismatch =
        "progenitor.json is present but its sha256 does not match the installable bytes; refused, not downgraded to a listing";
    public const string InspectBadProgenitorJson = "progenitor.json is present but could not be parsed";
    public const string InspectContradictory = "progenitor.json is present but its metadata is contradictory";
    /// <summary>Mod compatibility C3: <c>compatibility.peers</c> in progenitor.json is not a valid range.</summary>
    public static string InspectBadPeers(string text, string? error) =>
        "progenitor.json compatibility.peers '" + text + "' is not a valid compatibility range: " + (error ?? "unreadable") + "; refused";

    /// <summary>
    /// Mod compatibility C3: progenitor.json's <c>compatibility.peers</c> and the image's <c>[NetworkCompatibility]</c>
    /// <c>Peers</c> disagree (one of them may be absent). Starts with <see cref="InspectContradictory"/>.
    /// </summary>
    public static string InspectPeersContradict(string? json, string? image) =>
        InspectContradictory + ": compatibility.peers is " + PeersWords(json) + " but the plugin's [NetworkCompatibility] Peers is "
        + PeersWords(image);

    private static string PeersWords(string? value)
    {
        string v = (value ?? "").Trim();
        return v.Length == 0 ? "not set" : "'" + v + "'";
    }

    public const string InspectTooManyEntries = "the zip has more entries than the inspection cap";
    public const string InspectNotZip = "the downloaded file is not a readable zip archive";

    public const string ListingOnly =
        "Listing only — not a Progenitor manifest. This row was adapted from a third-party listing. "
        + "Progenitor has not verified a hash, a side, or whether a restart is required.";

    public const string NotInstallableNoHash =
        "this listing has no sha256 of the installable bytes; browse only";
    public const string NotInstallableNoId =
        "this listing has no plugin GUID; Progenitor will not invent one from a Thunderstore {owner}-{name} and cannot name a pending file";
    public const string OrdinaryBepInExRestart =
        "this is an ordinary BepInEx package, not a progenitor.json-declared reloadable plugin; restart required at best";
    public const string OrdinaryBepInExBlocked =
        "this is an ordinary BepInEx package that the restart install path cannot place (no plugin GUID, or it would write into plugins/ or patchers/); install it with a mod manager. Progenitor will not write into BepInEx/plugins/";
    public const string PluginsLayoutBlocked =
        "the package layout targets BepInEx/plugins/, patchers/ or core/; this menu will not write there";
    public const string MissingMetadataNoHotLoad =
        "missing, contradictory or unsupported metadata; hot load is not offered";
    public const string ProgenitorDepDoesNotGrantHotLoad =
        "a Progenitor dependency alone never enables hot loading";
    /// <summary>INDEX1: a hotLoadable package from Thunderstore installs for the next start; only Index installs load now.</summary>
    public const string HotLoadIndexOnly =
        "installed from Thunderstore, so it loads at the next start; only mods installed from the Progenitor Index load without a restart";

    /// <summary>INDEX1 (M3): a member of an Index mod's dependencies that the Index pins nowhere.</summary>
    public static string UnpinnedDependency(string package) =>
        package + " is not pinned by the Progenitor Index, so this mod cannot be installed from it";

    /// <summary>INDEX1 (L4): a Thunderstore install's update while [Mods] UseThunderstore is off.</summary>
    public const string UpdateThunderstoreOff = "it was installed from Thunderstore, and Thunderstore is turned off";

    /// <summary>INDEX1: updates stay with their source.</summary>
    public const string UpdateIndexGone =
        "it was installed from the Progenitor Index, which no longer lists it; it is not updated from Thunderstore";

    /// <inheritdoc cref="UpdateIndexGone"/>
    public const string UpdateThunderstoreGone =
        "it was installed from Thunderstore, which no longer lists it; reinstall it from the Progenitor Index to move it there";

    /// <summary>INDEX1: a copy installed from Thunderstore is removed at the next start.</summary>
    public const string HotUnloadIndexOnly =
        "it was not installed from the Progenitor Index, so it is removed at the next start";

    public const string HotLoadableIsAuthorClaim =
        "hotLoadable is the author's claim, not proof of cleanup; the installer still decides";
    public const string EligibilityNotWired =
        "runtime eligibility is not wired; same-session load is not offered from this pipeline";
    public const string ExecutorNotImplemented =
        "Apply now: not yet available (executor); Confirm installs for next start";

    // ---- HOT1: the apply-now executor ------------------------------------------------------
    /// <summary>The plan's executor line when the hot-load executor will write this process's reload folder.</summary>
    public const string ExecutorHotLoad =
        "Apply now: the approved image is written into progenitor-reload/ in this process and the reloader loads it within a second; no restart";

    /// <summary>Apply now needs the process that will load the image; a remote server root cannot be hot loaded from here.</summary>
    public const string HotLoadNotThisProcess =
        "Apply now needs the process that loads the image; the selected local-server folder is not this process, so it installs for the server's next start";

    /// <summary>The Both target: the client half hot loads, the server half is a restart install.</summary>
    public const string HotLoadServerHalfPending =
        "the local-server half of this install is written to that server's pending folder and loads at its next start";

    /// <summary>ExecuteToPending was called with a plan whose admission never admitted a hot load.</summary>
    public const string HotLoadNotAdmitted =
        "this plan was not admitted for same-session load; nothing was written into progenitor-reload/";

    /// <summary>The reload copy was deleted, so the reloader unloads the plugin within a second.</summary>
    public const string HotUnloaded =
        "the reload copy was deleted; the reloader unloads the plugin within a second and nothing stays loaded";

    // ---- HOT1: the local index source ------------------------------------------------------
    public const string LocalIndexNotConfigured = "no local index folder is configured ([Install] LocalIndexPath is empty)";
    public const string LocalIndexMissing = "the local index folder has no index.json";
    public const string LocalIndexUnreadable = "the local index.json could not be read";
    /// <summary>TSTRAFFIC1: Thunderstore changelogs are not requested (nothing displays them).</summary>
    public const string ChangelogNotFetched = "the changelog is not fetched";

    public const string LocalDocumentUnavailable = "this local listing has no README.md or CHANGELOG.md beside its index.json";
    public const string FileTransportRefused = "this transport only serves file:// URLs from the configured local index folder";
    public const string FileTransportMissing = "the local file named by this URL does not exist";
    public const string AdmissionEligible =
        "admitted: this exact image is qualified and the running framework agreed";
    public const string AdmissionRefusedHotLoad =
        "same-session load refused by admission";
    public const string AdmissionUnknown =
        "same-session load undecided by admission";
    public const string AdmissionBlocked =
        "admission found no supported install path";
    public const string ApplyNowRequiresEligibility =
        "Apply now requires a verified progenitor.json, a hotLoadable declaration, and the running framework's eligibility decision";
    public const string NoConfiguredLocalServer =
        "Local server was selected but no validated local-server folder is configured";
    public const string BothRequiresExplicit =
        "Both is only used when the player explicitly selects it; metadata never expands the target";
    public const string DefaultTargetIsClient = "the default install target is the client";
    public const string DependenciesDoNotExpandTargets =
        "dependencies never expand the selected install targets";
    public const string ApprovalInvalid =
        "the approved plan's artifact hashes no longer match; approval is invalid";
    public const string ApprovalMissing = "this plan has not been approved";
    public const string PlanBlocked = "the plan is blocked and nothing will be written";
    public const string PreflightFailed = "preflight failed; nothing was written";
    public const string ReloadFolderForbidden =
        "this pipeline does not write into progenitor-reload/; same-session load is a later coordinator integration";
    public const string ChangedArtifact = "the artifact bytes changed after approval";
    public const string UnknownEntry = "no catalog entry with that key is loaded";
    public const string NotInspected = "download and inspect this listing before planning an install";
    public const string ReservedFileName = "the destination file name is a reserved Progenitor image";
    public const string FailedWriteNothing = "the write was refused; nothing was written";

    // ---- INSTORD1: the ordinary-package restart route -------------------------------------
    public const string OrdinaryNoDll =
        "this package contains no DLL; there is nothing for the restart install path to place";
    public const string OrdinaryTooManyDlls =
        "this package contains more DLLs than the inspection cap; install it with a mod manager";
    public const string OrdinaryForbiddenFolder =
        "this package places files under patchers/, core/ or config/; the restart install path only places a plugin DLL and the DLLs beside it";
    public const string OrdinaryDllElsewhere =
        "this package's DLLs are not all in one folder at the zip root or under BepInEx/plugins/; the restart install path will not place them";
    public const string OrdinaryNoEntryPoint =
        "no DLL in this package declares [BepInPlugin]; Progenitor will not invent a plugin GUID from a Thunderstore {owner}-{name}";
    public const string OrdinaryMultipleEntryPoints =
        "more than one DLL in this package declares [BepInPlugin]; the restart install path places exactly one plugin";
    public const string OrdinaryRestartRoute =
        "ordinary BepInEx package: the plugin DLL is placed in Progenitor's own reload folder at the next start, never in BepInEx/plugins/";
    public const string OrdinaryHotSwapRefused =
        "this plugin uses Harmony directly, so Progenitor will not hot swap it; it loads at the next start and unloads only at a restart";

    public static string OrdinaryExtraFile(string destination) =>
        "this package contains a file the restart install path would have to place ("
        + destination
        + "); only DLLs beside the plugin are placed, and documentation, images and source code are left out";

    /// <summary>INSTPLUG1: <see cref="OrdinaryForbiddenFolder"/> naming the entry that triggered it.</summary>
    public static string OrdinaryForbiddenFolderAt(string destination) =>
        "this package writes " + destination + " (under patchers/, core/ or config/); the restart install path only places a plugin DLL and the DLLs beside it";

    /// <summary>INSTPLUG1: <see cref="PluginsLayoutBlocked"/> naming the entry that triggered it.</summary>
    public static string PluginsLayoutBlockedAt(string destination) =>
        "the package writes " + destination + " under BepInEx/plugins/, patchers/ or core/ and is not a plugin this menu can place; it will not write there";

    public static string OrdinaryUnreadableImage(string fileName) =>
        "a DLL in this package is not a readable .NET assembly (" + fileName + "); nothing was inspected further";

    public static string OrdinaryProcessMismatch(string loadsIn, string executable) =>
        "this plugin declares [BepInProcess] and loads in " + loadsIn
        + "; the selected target runs " + executable + ", which it never loads in";

    /// <summary>The exact sentence a missing Thunderstore dependency shows.</summary>
    public static string OrdinaryRequires(string package) =>
        "requires " + package + " — install it first";

    // ---- DEPS1: the dependency closure -----------------------------------------------------

    /// <summary>A dependency string that is not <c>&lt;owner&gt;-&lt;name&gt;-&lt;version&gt;</c>.</summary>
    public static string ClosureMalformedDependency(string dependency) =>
        "\"" + (dependency ?? "") + "\" is not a Thunderstore <owner>-<name>-<version> dependency string, so its package cannot be resolved";

    /// <summary>A dependency the loaded index has no listing for: refused by name, never guessed at.</summary>
    public static string ClosureUnknownPackage(string package) =>
        "the package index has no listing for " + package + ", so it cannot be installed with this package";

    /// <summary>A dependency graph that comes back to a package it already contains.</summary>
    public static string ClosureCycle(string chain) =>
        "the dependencies of this package form a cycle (" + chain + "); nothing was planned";

    /// <summary>The index's newest version of a dependency is older than the version the listing asks for.</summary>
    public static string ClosureOlderThanListed(string package, string have, string want) =>
        "the package index carries " + package + " " + have + ", which is older than the required " + want + "; nothing was planned";

    /// <summary>The dependency graph is deeper than one closure walks.</summary>
    public static string ClosureTooDeep(string package) =>
        "the dependencies of " + package + " are nested deeper than " + PackageDependencyClosure.MaxDepth.ToString() + " levels; nothing was planned";

    /// <summary>The dependency graph names more packages than one closure installs at once.</summary>
    public static string ClosureTooMany(string package) =>
        "this package needs more than " + PackageDependencyClosure.MaxMembers.ToString() + " other packages (at " + package + "); nothing was planned";

    /// <summary>One member of the closure was refused, so the whole set is.</summary>
    public static string ClosureMemberRefused(string package, string reason) =>
        "Cannot install — " + package + " refused: " + (reason ?? "");

    /// <summary>A dependency whose own <c>[BepInProcess]</c> does not run on the chosen target is installed on the half it does run on.</summary>
    public static string ClosureMemberHalf(string loadsIn, string half) =>
        "this dependency declares [BepInProcess] and loads in " + loadsIn
        + ", which the chosen target does not run, so it is installed on the " + half + " instead";

    /// <summary>A closure member that is already on the target and is not planned again.</summary>
    public const string ClosureAlreadySatisfied =
        "already on this target, so it is not downloaded or planned again";

    /// <summary>One member of the closure failed to write; the members before it are real installs and stay.</summary>
    public static string ClosureMemberFailed(string package, string reason) =>
        "the install stopped at " + package + ": " + (reason ?? "");

    /// <summary>Remove refused because another ledgered install lists this package as a dependency.</summary>
    public static string RemoveHasDependents(string package, IReadOnlyList<string> dependents) =>
        package + " is a dependency of " + string.Join(", ", dependents)
        + "; removing it stops them from loading. Remove those first, or remove it anyway.";

    public const string RestartClient = "this cannot load until the client process restarts";
    public const string RestartServer = "this cannot load until the local server process restarts";
    public const string RestartBoth = "this cannot load until both the client and the local server restart";

    public const string ZipHashKind = "zip";
    public const string DllHashKind = "dll";

    /// <summary>
    /// Catalog HTTP user agent. The product token is this build's git-derived Progenitor version
    /// (docs/versioning.md), read from the assembly's informational version without its
    /// <c>+revision</c> suffix.
    /// </summary>
    public static readonly string UserAgent =
        "Progenitor/" + ProductVersion() + " (V Rising; catalog-install)";

    private static string ProductVersion()
    {
        string? v = null;
        try
        {
            v = typeof(InstallReasons).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch { }
        if (string.IsNullOrEmpty(v)) return "0.0.0.0";
        int plus = v!.IndexOf('+');
        return plus > 0 ? v.Substring(0, plus) : v;
    }

    public static string HttpStatus(int code) =>
        FetchHttpError + " (HTTP " + code.ToString() + ")";

    public static string ServerError(int code) =>
        FetchServerError + " (HTTP " + code.ToString() + ")";

    public static string ReservedNamed(string fileName) =>
        InspectReserved + " (" + fileName + ")";

    public static string DuplicateNamed(string destination) =>
        InspectDuplicate + " (" + destination + ")";
}
