using System;
using System.Collections.Generic;
using Progenitor.Chat;

namespace Progenitor.Ui;

/// <summary>Which page a mod link opens, decided by <see cref="ModLinks.Decide"/>.</summary>
internal enum ModLinkTarget
{
    /// <summary>The package id or version is not a valid mod link; nothing opens.</summary>
    Invalid = 0,

    /// <summary>The package is installed: its page in the Mods tab.</summary>
    Installed = 1,

    /// <summary>Not installed, listed in the catalog: its listing in Install Mods.</summary>
    Catalog = 2,

    /// <summary>Not installed, and the loaded catalog does not list it.</summary>
    Unknown = 3,

    /// <summary>Not installed, and Thunderstore is turned off in Preferences.</summary>
    SourceOff = 4,

    /// <summary>Not installed, and the catalog has no listings loaded (offline, or not fetched yet).</summary>
    Offline = 5,
}

/// <summary>An installed mod and the Thunderstore package it came from (<c>Namespace-Name</c>).</summary>
internal readonly struct ModLinkInstalled
{
    internal ModLinkInstalled(string pluginId, string package, string version)
    {
        PluginId = pluginId ?? "";
        Package = package ?? "";
        Version = version ?? "";
    }

    internal string PluginId { get; }
    internal string Package { get; }
    internal string Version { get; }
}

/// <summary>What the Mods menu knows when a link is resolved. Game-free, so the decision is tested with plain data.</summary>
internal sealed class ModLinkFacts
{
    /// <summary>Installed mods whose package is known (the install ledger, else the catalog listing that names the plugin).</summary>
    internal IReadOnlyList<ModLinkInstalled> Installed { get; init; } = Array.Empty<ModLinkInstalled>();

    /// <summary><c>[Mods] UseThunderstore</c>.</summary>
    internal bool CatalogEnabled { get; init; } = true;

    /// <summary>The catalog has listings loaded.</summary>
    internal bool CatalogLoaded { get; init; }

    /// <summary>An index load is running; an empty catalog may still fill.</summary>
    internal bool CatalogLoading { get; init; }

    /// <summary>Package id → the catalog key and the listed (newest) version, or null when not listed.</summary>
    internal Func<string, (string Key, string Version)?> Listing { get; init; } = _ => null;
}

/// <summary>The outcome of <see cref="ModLinks.Decide"/>: where to go, the status word and any notice to show.</summary>
internal readonly struct ModLinkDecision
{
    internal ModLinkDecision(ModLinkTarget target, string pluginId, string catalogKey, string status, string notice)
    {
        Target = target;
        PluginId = pluginId ?? "";
        CatalogKey = catalogKey ?? "";
        Status = status ?? "";
        Notice = notice ?? "";
    }

    internal ModLinkTarget Target { get; }

    /// <summary>The installed plugin to open (<see cref="ModLinkTarget.Installed"/>).</summary>
    internal string PluginId { get; }

    /// <summary>The catalog listing to open (<see cref="ModLinkTarget.Catalog"/>).</summary>
    internal string CatalogKey { get; }

    /// <summary><c>opened installed</c>, <c>opened catalog</c> or <c>not found: …</c>.</summary>
    internal string Status { get; }

    /// <summary>A sentence for the notice dialog, or empty when nothing needs saying.</summary>
    internal string Notice { get; }

    internal bool Opens => Target is ModLinkTarget.Installed or ModLinkTarget.Catalog;
}

/// <summary>What <see cref="ModLinks"/> needs from the game and the Mods menu. Tests substitute a fake.</summary>
internal interface IModLinkHost
{
    bool WorkspaceOpen { get; }

    /// <summary>Open the Mods workspace the way its shortcut does (in a world through the game's menu, so it may finish later).</summary>
    void OpenWorkspace();

    /// <summary>Read once the workspace is open (the catalog and the install ledger are loaded by the open).</summary>
    ModLinkFacts Facts();

    void OpenInstalled(string pluginId);

    void OpenCatalog(string catalogKey);

    bool CopyToClipboard(string text);

    void Notice(string title, string body);
}

/// <summary>
/// Chat stage 3 (mod links), client: open a shared mod in the Mods menu, and share one into chat. Opening
/// a link only navigates: the installed mod's page in the Mods tab when its Thunderstore package is
/// installed, otherwise that package's listing in Install Mods. It never downloads, installs, enables,
/// updates or changes a target or a source. Only a package id and version that passed
/// <see cref="ModLinkToken"/>'s validation reach it; anything else is refused with a notice.
///
/// <para>A link clicked while the workspace is closed opens it first; in a world that open goes through the
/// game's menu and finishes on a later frame, so the navigation waits in <see cref="Tick"/> (called from
/// <see cref="Keybinds.Pump"/>) for at most <see cref="OpenTimeoutFrames"/>. A share in a world closes the
/// workspace and puts the token in the chat input once the game's menu is down.</para>
/// </summary>
internal static class ModLinks
{
    internal const string StatusInstalled = "opened installed";
    internal const string StatusCatalog = "opened catalog";
    internal const string StatusOpening = "opening";
    internal const string StatusNotFoundPrefix = "not found: ";
    internal const string StatusInvalid = StatusNotFoundPrefix + "not a valid mod link";

    internal const string ShareCopied = "copied";
    internal const string ShareCopyFailed = "copy failed";
    internal const string ShareInvalid = "invalid";

    internal const string NoticeTitle = "Mod link";
    internal const string ShareTitle = "Mod link copied";

    /// <summary>The caption of the share control on the mod page and the Install drawer.</summary>
    internal const string ShareCaption = "Copy link";

    /// <summary>The share control on a list row (shown while the pointer is on the row).</summary>
    internal const string ShareRowCaption = "Share";

    internal static string CopiedNotice(string package) =>
        "A link to " + package + " is on your clipboard. Paste it in chat (Ctrl+V); players with Progenitor can click it to open the mod.";
    internal const string CopyUnavailablePrefix = "Copying is not available here. The link is: ";
    internal const string InvalidNotice = "That mod link is not valid, so nothing was opened.";

    /// <summary>How long a link waits for the workspace to open (and an empty catalog to load), about 10 s at 60 fps.</summary>
    internal const int OpenTimeoutFrames = 600;

    internal static string UnknownNotice(string package) =>
        "No Thunderstore listing named " + package + " was found, so nothing was opened.";

    internal static string SourceOffNotice(string package) =>
        package + " is not installed, and Thunderstore is turned off in Preferences, so its listing cannot be shown.";

    internal static string OfflineNotice(string package) =>
        package + " is not installed, and the Thunderstore mod list is not loaded (offline?). Try again once Install Mods shows the list.";

    internal static string InstalledVersionNotice(string package, string requested, string installed) =>
        "This link names " + package + " " + requested + "; you have "
        + (installed.Length > 0 ? installed : "another version") + " installed.";

    internal static string CatalogVersionNotice(string package, string requested, string listed) =>
        "This link names " + package + " " + requested + "; the newest listed version is "
        + (listed.Length > 0 ? listed : "unknown") + ". Nothing is installed by opening it.";

    /// <summary>The production host (the Mods menu, the chat window and the clipboard). Tests replace it.</summary>
    internal static IModLinkHost Host { get; set; } = DefaultHost.Instance;

    /// <summary>The last status <see cref="Open"/> or a later frame produced (readback and tests).</summary>
    internal static string LastStatus { get; private set; } = "";

    /// <summary>The last share outcome (readback and tests).</summary>
    internal static string LastShare { get; private set; } = "";

    private static (string Package, string? Version)? _pendingOpen;
    private static int _openFrames;
    private static bool _openRequested;

    /// <summary>
    /// Decide where a link goes. Installed first (the package matches an installed mod's, ignoring case),
    /// then the catalog when Thunderstore is on and loaded. A requested version that differs from the
    /// installed or listed one still opens the page, with a notice saying so.
    /// </summary>
    internal static ModLinkDecision Decide(string package, string? version, ModLinkFacts facts)
    {
        string p = (package ?? "").Trim();
        string v = (version ?? "").Trim();
        if (!ModLinkToken.IsPackage(p) || (v.Length > 0 && !ModLinkToken.IsVersion(v)))
            return new ModLinkDecision(ModLinkTarget.Invalid, "", "", StatusInvalid, InvalidNotice);
        facts ??= new ModLinkFacts();

        ModLinkInstalled? match = null;
        foreach (var installed in facts.Installed)
        {
            if (!string.Equals(installed.Package, p, StringComparison.OrdinalIgnoreCase) || installed.PluginId.Length == 0) continue;
            // Several plugins of one package: the one at the requested version, else the first.
            if (match == null || (v.Length > 0 && string.Equals(installed.Version, v, StringComparison.Ordinal)))
                match = installed;
        }

        if (match is { } hit)
        {
            string note = v.Length > 0 && !string.Equals(hit.Version, v, StringComparison.Ordinal)
                ? InstalledVersionNotice(p, v, hit.Version)
                : "";
            return new ModLinkDecision(ModLinkTarget.Installed, hit.PluginId, "", StatusInstalled, note);
        }

        if (!facts.CatalogEnabled)
            return new ModLinkDecision(ModLinkTarget.SourceOff, "", "", StatusNotFoundPrefix + p + " (Thunderstore is off)", SourceOffNotice(p));

        (string Key, string Version)? listed = null;
        try { listed = facts.Listing(p); }
        catch { listed = null; }
        if (listed is { } entry && entry.Key.Length > 0)
        {
            string note = v.Length > 0 && !string.Equals(entry.Version, v, StringComparison.Ordinal)
                ? CatalogVersionNotice(p, v, entry.Version)
                : "";
            return new ModLinkDecision(ModLinkTarget.Catalog, "", entry.Key, StatusCatalog, note);
        }

        if (!facts.CatalogLoaded)
            return new ModLinkDecision(ModLinkTarget.Offline, "", "", StatusNotFoundPrefix + p + " (mod list not loaded)", OfflineNotice(p));
        return new ModLinkDecision(ModLinkTarget.Unknown, "", "", StatusNotFoundPrefix + p, UnknownNotice(p));
    }

    /// <summary>
    /// Open the Mods menu on the mod a link names: its installed page, else its Install Mods listing. Never
    /// downloads, installs, enables or changes anything. Returns <c>opened installed</c>, <c>opened catalog</c>,
    /// <c>not found: …</c> (with a notice), or <c>opening</c> while the workspace is still opening (the result
    /// follows on a later frame, a notice included when it is not found).
    /// </summary>
    internal static string Open(string package, string? version)
    {
        string p = (package ?? "").Trim();
        string? v = string.IsNullOrWhiteSpace(version) ? null : version!.Trim();
        if (!ModLinkToken.IsPackage(p) || (v != null && !ModLinkToken.IsVersion(v)))
        {
            ShowNotice(NoticeTitle, InvalidNotice);
            return LastStatus = StatusInvalid;
        }

        _pendingOpen = (p, v);
        _openFrames = 0;
        _openRequested = false;
        return StepOpen() ?? (LastStatus = StatusOpening);
    }

    /// <summary>
    /// The Share control: the link goes to the clipboard and a notice says so; the Mods menu stays open (user feedback
    /// 2026-10-07: closing the menu to fill the chat input landed on the game's System menu instead). Returns
    /// <c>copied</c>, <c>copy failed</c> (the notice then shows the link to type) or <c>invalid</c>.
    /// </summary>
    internal static string Share(string package, string? version)
    {
        string token = ModLinkToken.Format(package, version);
        if (token.Length == 0) return LastShare = ShareInvalid;
        return LastShare = Copy(token, CopiedNotice(package));
    }

    /// <summary>Once a frame on the client: finish a link waiting for the workspace, and a share waiting for the menu to close.</summary>
    internal static void Tick()
    {
        try
        {
            if (_pendingOpen != null) StepOpen();
            if (_pendingThen != null) StepThen();
        }
        catch (Exception e)
        {
            _pendingOpen = null;
            _pendingThen = null;
            Plugin.Logger?.LogWarning("Ui: mod link tick threw: " + e.Message);
        }
    }

    private static Action? _pendingThen;
    private static int _thenFrames;
    private static bool _thenRequested;

    /// <summary>
    /// Open the Mods menu the way a link does (in a world through the game's menu, which finishes on a later frame), then
    /// run <paramref name="then"/> once it is open; dropped after <see cref="OpenTimeoutFrames"/>. Runs at once when the
    /// menu is already open. Used by the chat's All chat settings (2026-10-07: opening directly left the player on the
    /// game's menu at its spellbook page).
    /// </summary>
    internal static void OpenWorkspaceThen(Action then)
    {
        if (Host.WorkspaceOpen)
        {
            then();
            return;
        }

        // Already waiting for the menu (a double press, or a second caller): the newer continuation replaces the older
        // one, and the open is not asked for again, since a second request while the game's menu opens cancels it.
        if (_pendingThen != null)
        {
            _pendingThen = then;
            return;
        }

        _pendingThen = then;
        _thenFrames = 0;
        _thenRequested = false;
        StepThen();
    }

    private static void StepThen()
    {
        if (_pendingThen is not { } then) return;
        if (Host.WorkspaceOpen)
        {
            _pendingThen = null;
            then();
            return;
        }

        if (++_thenFrames > OpenTimeoutFrames)
        {
            _pendingThen = null;
            Plugin.Logger?.LogWarning("Ui: the Mods menu did not open for a link or a page request.");
            return;
        }

        if (_thenRequested) return;
        _thenRequested = true;
        try { Host.OpenWorkspace(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: opening the Mods menu threw: " + e.Message); }
    }

    /// <summary>Drop anything pending (tests, and a Core unload).</summary>
    internal static void Reset()
    {
        _pendingOpen = null;
        _pendingThen = null;
        _openFrames = 0;
        _openRequested = false;
        LastStatus = "";
        LastShare = "";
    }

    /// <summary>A link in progress: open the workspace, then decide and navigate. Null while still waiting.</summary>
    private static string? StepOpen()
    {
        if (_pendingOpen is not { } link) return null;
        bool timedOut = ++_openFrames > OpenTimeoutFrames;
        if (!Host.WorkspaceOpen)
        {
            if (timedOut)
            {
                _pendingOpen = null;
                Plugin.Logger?.LogWarning("Ui: mod link: the Mods menu did not open for " + link.Package + ".");
                return LastStatus = StatusNotFoundPrefix + link.Package + " (the Mods menu did not open)";
            }

            if (_openRequested) return null;
            _openRequested = true;
            try { Host.OpenWorkspace(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod link: opening the Mods menu threw: " + e.Message); }
            if (!Host.WorkspaceOpen) return null;
        }

        var facts = Host.Facts();
        var decision = Decide(link.Package, link.Version, facts);
        // The index is loaded by the open: an empty catalog that is still loading gets its chance.
        if (decision.Target == ModLinkTarget.Offline && facts.CatalogLoading && !timedOut) return null;

        _pendingOpen = null;
        if (decision.Target == ModLinkTarget.Installed) Host.OpenInstalled(decision.PluginId);
        else if (decision.Target == ModLinkTarget.Catalog) Host.OpenCatalog(decision.CatalogKey);
        if (decision.Notice.Length > 0) ShowNotice(NoticeTitle, decision.Notice);
        Plugin.Logger?.LogInfo("Ui: mod link " + link.Package + (link.Version != null ? "@" + link.Version : "") + ": " + decision.Status);
        return LastStatus = decision.Status;
    }

    private static string Copy(string token, string notice)
    {
        bool copied;
        try { copied = Host.CopyToClipboard(token); }
        catch { copied = false; }
        ShowNotice(ShareTitle, copied ? notice : CopyUnavailablePrefix + token);
        return copied ? ShareCopied : ShareCopyFailed;
    }

    private static void ShowNotice(string title, string body)
    {
        try { Host.Notice(title, body); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod link notice threw: " + e.Message); }
    }

    /// <summary>The production host.</summary>
    private sealed class DefaultHost : IModLinkHost
    {
        internal static readonly DefaultHost Instance = new();

        public bool WorkspaceOpen => ModsMenu.IsOpen;

        public void OpenWorkspace() => ModsMenu.OpenForModLink();

        public ModLinkFacts Facts() => ModsMenu.ModLinkFacts();

        public void OpenInstalled(string pluginId) => ModsMenu.OpenModLinkInstalled(pluginId);

        public void OpenCatalog(string catalogKey) => ModsMenu.OpenModLinkCatalog(catalogKey);

        public bool CopyToClipboard(string text)
        {
            // The bridge's test clipboard while one is armed, never the player's (as the join screens copy).
            if (UiTextInjection.Clipboard != null)
            {
                UiTextInjection.Clipboard = text ?? "";
                return true;
            }

            return Ui.TryCopyToClipboard(text ?? "");
        }

        public void Notice(string title, string body) =>
            UiDialogs.Notice(Instance, SettingsPanel.PluginId, Ui.FrameworkFooter, title, body, "OK", null);
    }
}
