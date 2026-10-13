using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.PluginActions;

namespace Progenitor.Install;

/// <summary>
/// SRVUNINST1: a server mod addressed by its plugin GUID instead of a Thunderstore package. The
/// remote channel's package field carries <c>plugin:&lt;guid&gt;</c> for these, in both directions: a
/// status row the server identified by plugin, and an Uninstall request for it. No field or byte of
/// the wire changes. A Thunderstore key can never contain the colon, so the two spellings cannot be
/// confused, and an older peer reads one as a package it does not know: an older client matches no
/// listing to the row, an older server refuses the request as <c>bad-package</c>.
/// </summary>
internal static class RemotePluginRef
{
    public const string Prefix = "plugin:";

    /// <summary>The longest GUID that still fits the package field with its prefix.</summary>
    public const int MaxGuidChars = RemoteInstallWire.MaxPackageChars - 7;

    // BepInEx GUIDs are reverse-domain style in practice; spaces and other punctuation are refused
    // rather than escaped, so a GUID that needs them is simply not addressable remotely.
    private static readonly Regex GuidPattern = new("^[A-Za-z0-9_][A-Za-z0-9_.\\-]{0,120}$", RegexOptions.CultureInvariant);

    public static bool IsValidGuid(string? guid) =>
        !string.IsNullOrEmpty(guid) && guid!.Length <= MaxGuidChars && GuidPattern.IsMatch(guid);

    /// <summary><c>plugin:&lt;guid&gt;</c>, or empty when the GUID cannot be addressed.</summary>
    public static string Of(string? guid)
    {
        string g = (guid ?? "").Trim();
        return IsValidGuid(g) ? Prefix + g : "";
    }

    public static bool IsRef(string? package) =>
        package != null && package.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The GUID of a well-formed plugin reference.</summary>
    public static bool TryGuid(string? package, out string guid)
    {
        guid = "";
        if (!IsRef(package)) return false;
        string g = package!.Substring(Prefix.Length);
        if (!IsValidGuid(g)) return false;
        guid = g;
        return true;
    }
}

/// <summary>
/// SRVUNINST1: what the server would do with an Uninstall of one of its plugins. The order is the
/// user's rule of 2026-09-29, applied to the server's own files: the install ledger first, then a
/// top-level DLL in <c>progenitor-reload</c> that declares the plugin (FWALLOW1), then an ordinary
/// plugin in <c>BepInEx/plugins</c> through the preloader's next-start request.
/// </summary>
internal enum ServerPluginRemoval : byte
{
    /// <summary>The server cannot tell which files are the mod's (or must keep it): nothing is offered.</summary>
    None = 0,

    /// <summary>A ledgered install: the package uninstall runs (marked for the next start, or unloaded now).</summary>
    Ledger = 1,

    /// <summary>A reload-folder DLL: deleted now; the server's reloader unloads it.</summary>
    ReloadNow = 2,

    /// <summary>An ordinary plugin: the preloader moves its files to <c>progenitor-removed</c> at the next start.</summary>
    NextStart = 3,

    /// <summary>An ordinary plugin whose removal is already requested for the next start.</summary>
    RemovalPending = 4,
}

/// <summary>SRVUNINST1: one plugin the server knows of, as the server's own evidence describes it.</summary>
internal sealed class ServerPluginFact
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary>The loaded assembly's full path (the chainloader's report), or empty.</summary>
    public string Location { get; init; } = "";

    /// <summary>The one top-level DLL in <c>progenitor-reload</c> that declares it, or empty.</summary>
    public string ReloadFile { get; init; } = "";

    /// <summary>Progenitor's own plugin (<see cref="Ui.ModMenuInventory.IsFrameworkId"/>): never removed.</summary>
    public bool Framework { get; init; }

    /// <summary>The ordinary-plugin identification (<see cref="PluginFileActions.Identify"/>), or null.</summary>
    public PluginIdentification? Plain { get; init; }

    /// <summary>A preloader Remove request for it is waiting for the next start.</summary>
    public bool RemovalRequested { get; init; }

    /// <summary>The plugin GUIDs this one hard-depends on (the chainloader's report; empty for a reload-only fact).</summary>
    public IReadOnlyList<string> HardDependencies { get; init; } = Array.Empty<string>();

    /// <summary>Other plugins the same reload-folder DLL declares: deleting it would take them too.</summary>
    public IReadOnlyList<string> AlsoDeclares { get; init; } = Array.Empty<string>();

    /// <summary>Loaded plugins that hard-depend on this one (a removal breaks them; only <c>force</c> overrides).</summary>
    public IReadOnlyList<string> NeededBy { get; init; } = Array.Empty<string>();

    /// <summary>The chainloader loaded it from <c>BepInEx/plugins</c> (its <see cref="Location"/> is under that folder).</summary>
    public bool LoadedFromPlugins { get; init; }
}

/// <summary>SRVUNINST1: the decision for one plugin, with the ledger package it belongs to when ledgered.</summary>
internal readonly struct ServerPluginVerdict
{
    public ServerPluginVerdict(ServerPluginRemoval removal, string why = "", string package = "", RemoteRowState ledgerState = RemoteRowState.None)
    {
        Removal = removal;
        Why = why ?? "";
        Package = package ?? "";
        LedgerState = ledgerState;
    }

    public ServerPluginRemoval Removal { get; }

    /// <summary>Why nothing can be removed; empty otherwise.</summary>
    public string Why { get; }

    /// <summary>The ledger's package for <see cref="ServerPluginRemoval.Ledger"/>.</summary>
    public string Package { get; }

    public RemoteRowState LedgerState { get; }
}

/// <summary>SRVUNINST1: one ledgered file on the server, by absolute path.</summary>
internal readonly struct ServerLedgerFile
{
    public ServerLedgerFile(string absolutePath, string package, RemoteRowState state)
    {
        AbsolutePath = absolutePath ?? "";
        Package = package ?? "";
        State = state;
    }

    public string AbsolutePath { get; }
    public string Package { get; }
    public RemoteRowState State { get; }
}

/// <summary>SRVUNINST1: the server's identification of its plugins, game-free.</summary>
internal static class ServerPluginRemovals
{
    public const string WhyFramework = "Progenitor needs it to run";
    public const string WhyNoFiles = "the server cannot tell which files are this mod's";

    public static string WhyAlsoDeclares(IReadOnlyList<string> others) =>
        "its DLL in progenitor-reload also holds " + string.Join(", ", others) + ", which would go with it";

    public const string WhyTwoCopies = "it has a copy in progenitor-reload and another in BepInEx/plugins";

    /// <summary>
    /// The decision for one plugin. Progenitor's own plugins are never offered; a file the install
    /// ledger names takes the package uninstall; then the reload-folder file; then a waiting
    /// preloader request; then an ordinary plugin the identification is certain about. Anything else
    /// is managed elsewhere, with the identification's own reason.
    /// </summary>
    public static ServerPluginVerdict Decide(ServerPluginFact fact, IReadOnlyList<ServerLedgerFile>? ledger)
    {
        if (fact == null || string.IsNullOrWhiteSpace(fact.Id)) return new ServerPluginVerdict(ServerPluginRemoval.None, WhyNoFiles);
        if (fact.Framework) return new ServerPluginVerdict(ServerPluginRemoval.None, WhyFramework);
        if (ledger != null)
        {
            foreach (var file in ledger)
            {
                if (file.AbsolutePath.Length == 0 || file.Package.Length == 0) continue;
                if (SamePath(file.AbsolutePath, fact.ReloadFile) || SamePath(file.AbsolutePath, fact.Location))
                    return new ServerPluginVerdict(ServerPluginRemoval.Ledger, "", file.Package, file.State);
            }
        }

        if (fact.ReloadFile.Length > 0)
        {
            // SRVUNINST1 review: never a delete that takes other plugins along, and never a "removed" that
            // leaves a second copy loading from BepInEx/plugins.
            if (fact.AlsoDeclares.Count > 0) return new ServerPluginVerdict(ServerPluginRemoval.None, WhyAlsoDeclares(fact.AlsoDeclares));
            if (fact.LoadedFromPlugins)
                return new ServerPluginVerdict(ServerPluginRemoval.None, WhyTwoCopies);
            return new ServerPluginVerdict(ServerPluginRemoval.ReloadNow);
        }
        if (fact.RemovalRequested) return new ServerPluginVerdict(ServerPluginRemoval.RemovalPending);
        if (fact.Plain is { Certain: true }) return new ServerPluginVerdict(ServerPluginRemoval.NextStart);
        string why = (fact.Plain?.Why ?? "").Trim().TrimEnd('.');
        return new ServerPluginVerdict(ServerPluginRemoval.None, why.Length > 0 ? why : WhyNoFiles);
    }

    /// <summary>
    /// The status row's state for a verdict: a ledgered plugin reports its package's state; a reload
    /// file is <see cref="RemoteRowState.Hot"/> (removed now); an ordinary plugin is
    /// <see cref="RemoteRowState.Loaded"/> (removed at the next start); a waiting request is
    /// <see cref="RemoteRowState.UninstallPending"/>; nothing removable is <see cref="RemoteRowState.None"/>.
    /// </summary>
    public static RemoteRowState RowState(in ServerPluginVerdict verdict) => verdict.Removal switch
    {
        ServerPluginRemoval.Ledger => verdict.LedgerState,
        ServerPluginRemoval.ReloadNow => RemoteRowState.Hot,
        ServerPluginRemoval.NextStart => RemoteRowState.Loaded,
        ServerPluginRemoval.RemovalPending => RemoteRowState.UninstallPending,
        _ => RemoteRowState.None,
    };

    /// <summary>
    /// The plugin rows the status carries after the ledger rows: one per non-framework plugin whose
    /// GUID can be addressed, so a client that has no listing for the mod can still find it.
    /// </summary>
    public static List<RemoteServerRow> Rows(IReadOnlyList<ServerPluginFact>? facts, IReadOnlyList<ServerLedgerFile>? ledger)
    {
        var rows = new List<RemoteServerRow>();
        if (facts == null) return rows;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            if (fact == null || fact.Framework) continue;
            string package = RemotePluginRef.Of(fact.Id);
            if (package.Length == 0 || !seen.Add(fact.Id)) continue;
            var verdict = Decide(fact, ledger);
            rows.Add(new RemoteServerRow(package, Clip(fact.Version), RowState(verdict), ""));
        }

        return rows;
    }

    /// <summary>
    /// The preloader request for an ordinary plugin's removal at the next start — the same request
    /// the client's own Mods page writes (<c>ModSwitch.RequestRemove</c>) — or null without a certain
    /// identification.
    /// </summary>
    public static PluginFileRequest? RemoveRequest(ServerPluginFact fact, DateTime nowUtc)
    {
        if (fact?.Plain is not { Certain: true } plain) return null;
        return new PluginFileRequest
        {
            PluginId = fact.Id,
            Name = fact.Name.Length > 0 ? fact.Name : fact.Id,
            Action = PluginFileAction.Remove,
            Folder = plain.Folder,
            Files = plain.Files,
            Loose = plain.Loose,
            RequestedUtc = nowUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string Clip(string? version)
    {
        string v = (version ?? "").Trim();
        return v.Length <= RemoteInstallWire.MaxVersionChars ? v : v.Substring(0, RemoteInstallWire.MaxVersionChars);
    }

    private static bool SamePath(string a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b!), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

/// <summary>
/// SRVUNINST1: the remote uninstall refusals the admin may override. The server refuses an uninstall
/// whose dependency check could not run (its package index could not be loaded) or found other
/// installed mods that need it; the request's <c>force</c> flag skips that check. The client offers
/// that as <c>Uninstall anyway</c> with its own confirmation, never silently.
/// </summary>
internal static class RemoteUninstallForce
{
    public const string IndexUnreadablePrefix = "the server could not load Thunderstore's package index: ";

    /// <summary>An older server's wording of the same refusal.</summary>
    private const string OlderServerHint = "uninstall again with force";

    public const string AnywayCaption = "Uninstall anyway";

    /// <summary>The cell's short word for a refused uninstall.</summary>
    public const string RefusedWord = "Uninstall refused";

    public static bool Offers(RemoteInstallProgress? progress) =>
        progress != null
        && progress.Op == RemoteInstallOp.Uninstall
        && progress.Stage == RemoteInstallStage.Refused
        && (progress.Reason == RemoteInstallReason.HasDependents
            || (progress.Reason == RemoteInstallReason.Unavailable
                && (progress.Detail.StartsWith(IndexUnreadablePrefix, StringComparison.Ordinal)
                    || progress.Detail.IndexOf(OlderServerHint, StringComparison.OrdinalIgnoreCase) >= 0)));

    /// <summary>The <c>Uninstall anyway</c> confirmation's body: what the skipped check would have said.</summary>
    public static string ConfirmBody(RemoteInstallProgress? progress) =>
        progress?.Reason == RemoteInstallReason.HasDependents
            ? "Other mods on the server need it (" + (progress.Detail ?? "").Replace("needed by ", "") + "). Uninstall anyway removes it "
              + "without the dependency check; those mods may stop working."
            : "The server could not check what else needs it. Uninstall anyway removes it without the dependency check.";
}

/// <summary>
/// F16 (live 2026-10-07): a listing the connected server's own local test index (<c>[Install] LocalIndexPath</c> on the
/// server) offers, told to an allowed admin as a status row whose package is <c>local:&lt;key&gt;</c> and whose version is the
/// version that index has. No field or byte of the wire changes; an older client matches no listing to the row. Bounded by
/// <see cref="MaxOffers"/> and by the status's row cap (ledger rows first).
/// </summary>
internal static class RemoteLocalOffer
{
    public const string Prefix = "local:";

    public const int MaxOffers = 32;

    public static string Of(string? key)
    {
        string k = (key ?? "").Trim();
        // Review (L4): the wire limit is in UTF-8 bytes.
        return k.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(Prefix + k) > RemoteInstallWire.MaxPackageChars || k.IndexOf(':') >= 0 ? "" : Prefix + k;
    }

    public static bool IsOffer(string? package) =>
        package != null && package.StartsWith(Prefix, StringComparison.Ordinal);

    public static string KeyOf(string? package) => IsOffer(package) ? package!.Substring(Prefix.Length) : "";
}
