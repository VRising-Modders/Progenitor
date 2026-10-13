using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Progenitor.Support;

/// <summary>One loaded plugin as summary.txt lists it.</summary>
internal readonly struct SupportPlugin
{
    public SupportPlugin(string id, string name, string version)
    {
        Id = id ?? "";
        Name = name ?? "";
        Version = version ?? "";
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
}

/// <summary>
/// SUPPORT1: what the running game knows that the files do not: versions, whether it is in a world, the loaded
/// plugins and the current mod problems. Read on the main thread by the live half (<c>SupportLive</c>), formatted here.
/// </summary>
internal sealed class SupportFacts
{
    public string Side { get; set; } = "client";
    public string CoreVersion { get; set; } = "";
    public string? BootstrapVersion { get; set; }
    public string? DevVersion { get; set; }
    public string GameBuild { get; set; } = "";
    public string BepInExVersion { get; set; } = "";
    public string World { get; set; } = "";
    public bool HostingPrivateGame { get; set; }
    public List<SupportPlugin> Plugins { get; } = new();
    public List<string> Reloadable { get; } = new();
    public List<SupportPlugin> NetworkMods { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>The game part of summary.txt.</summary>
    internal string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Progenitor Core: " + Or(CoreVersion));
        sb.AppendLine("Progenitor Bootstrap: " + Or(BootstrapVersion));
        sb.AppendLine("Progenitor Dev bridge: " + (string.IsNullOrWhiteSpace(DevVersion) ? "not loaded" : DevVersion));
        sb.AppendLine("Game build: " + Or(GameBuild));
        sb.AppendLine("BepInEx: " + Or(BepInExVersion));
        sb.AppendLine("World: " + Or(World));
        if (Side == "client") sb.AppendLine("Hosting a private game: " + (HostingPrivateGame ? "yes" : "no"));

        sb.AppendLine();
        sb.AppendLine("BepInEx plugins loaded (" + Plugins.Count.ToString(CultureInfo.InvariantCulture) + "):");
        if (Plugins.Count == 0) sb.AppendLine("  (none)");
        foreach (var p in Sorted(Plugins)) sb.AppendLine("  " + Line(p));

        sb.AppendLine();
        sb.AppendLine("Progenitor reloadable plugins (" + Reloadable.Count.ToString(CultureInfo.InvariantCulture) + "):");
        if (Reloadable.Count == 0) sb.AppendLine("  (none)");
        var reloadable = new List<string>(Reloadable);
        reloadable.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string r in reloadable) sb.AppendLine("  " + r);

        if (NetworkMods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Mods in Progenitor's join handshake (" + NetworkMods.Count.ToString(CultureInfo.InvariantCulture) + "):");
            foreach (var p in Sorted(NetworkMods)) sb.AppendLine("  " + Line(p));
        }

        sb.AppendLine();
        sb.AppendLine("Mod problems (" + Problems.Count.ToString(CultureInfo.InvariantCulture) + "):");
        if (Problems.Count == 0) sb.AppendLine("  (none)");
        foreach (string p in Problems) sb.AppendLine("  " + p);

        if (Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (string n in Notes) sb.AppendLine("Note: " + n);
        }
        return sb.ToString();
    }

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value!.Trim();

    private static string Line(SupportPlugin p)
    {
        string head = p.Id.Length > 0 ? p.Id : "?";
        if (p.Name.Length > 0 && !string.Equals(p.Name, p.Id, StringComparison.Ordinal)) head += " (" + p.Name + ")";
        return head + " " + (p.Version.Length > 0 ? p.Version : "version unknown");
    }

    private static List<SupportPlugin> Sorted(List<SupportPlugin> list)
    {
        var copy = new List<SupportPlugin>(list);
        copy.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return copy;
    }
}

/// <summary>
/// SUPPORT1: the client's <b>Get help</b> group on Progenitor's page (<see cref="Ui.ProgenitorPage"/>): <b>Collect logs</b>
/// builds the bundle off the main thread, then opens Explorer with the zip selected and puts its path on the clipboard;
/// <b>Open the logs folder</b>; and the newest bundle. Game-free: the game and OS calls are seams the live half sets.
/// </summary>
internal static class SupportActions
{
    internal const string GroupTitle = "Get help";

    internal const string CollectCaption = "Collect logs";
    internal const string CollectLabel = "Collect logs for a help post";
    internal const string CollectHelp =
        "Saves one .zip on your Desktop with the logs that help the developer of whichever mod is misbehaving: BepInEx's log "
        + "(this session and the two before it), the game's own log, a list of your mods, a few settings, and the logs of a "
        + "private game or dedicated server running on this PC. Then it opens its folder. SteamIDs, IP addresses, your Windows user name, "
        + "passwords and tokens are removed; character and server names stay, so glance at it before posting. Post it in "
        + "#technical-support on the V Rising Mod Community Discord.";

    internal const string OpenCaption = "Open folder";
    internal const string OpenLabel = "Open the logs folder";
    internal const string OpenHelp =
        "Opens the folder Collect logs saves to: your Desktop, or the folder set below. BepInEx/progenitor-support, the "
        + "fallback, keeps its five newest bundles; other folders are never cleaned up.";

    internal const string FolderLabel = "Save bundles in";
    internal const string FolderHelp =
        "The folder Collect logs saves to. Empty: your Desktop. When it cannot be written, the bundle goes to "
        + "BepInEx/progenitor-support and the Last bundle line says why. A Desktop synced by OneDrive uploads the zip to "
        + "your cloud too; it is already redacted.";

    /// <summary><c>[Support] OutputFolder</c> in Core's ui cfg (client only).</summary>
    internal const string SettingsSection = "Support";
    internal const string OutputFolderKey = "OutputFolder";

    /// <summary><c>[Support] IncludeServerLogs</c>: an admin's Collect logs also fetches the joined server's bundle. On by default.</summary>
    internal const string IncludeServerLogsKey = "IncludeServerLogs";
    internal const string IncludeServerLogsLabel = "Include this server's logs";
    internal const string IncludeServerLogsHelp =
        "You are an admin of this server: Collect logs also asks it for its own bundle (its logs, redacted on the server) and "
        + "puts it under server/ in the same zip. Up to 20 MB, sent slowly enough not to disturb the game.";

    /// <summary>Tests: replaces whether this player is an admin of a joined Progenitor server (the row's showIf).</summary>
    internal static Func<bool>? ServerAdminProbe { get; set; }

    /// <summary>Whether the Include this server's logs row is shown: joined to a Progenitor server, and an admin there.</summary>
    internal static bool ServerLogsRowShown()
    {
        try
        {
            if (ServerAdminProbe != null) return ServerAdminProbe();
            return string.Equals(Networking.Handshake.LastClientResult, "ok", StringComparison.Ordinal) && Networking.JoinAdminRows.IsAdmin();
        }
        catch { return false; }
    }

    internal const string LastLabel = "Last bundle";
    internal const string StatusLabel = "Collect logs status";

    internal const string Collecting = "Collecting logs...";
    internal const string NoneYet = "None yet.";

    // ---- seams (the live half sets them; tests replace them) -------------------------------------------------------

    /// <summary>Main thread: the request for this process (game facts read now). Null: collecting is unavailable here.</summary>
    internal static Func<SupportBundleRequest?>? Gather { get; set; }

    /// <summary>Runs the file work off the main thread. Null: inline.</summary>
    internal static Func<Action, Task>? RunOff { get; set; }

    /// <summary>Back to the main thread. Null: inline.</summary>
    internal static Action<Action>? PostMain { get; set; }

    /// <summary>Opens Explorer with the file selected; false when it could not.</summary>
    internal static Func<string, bool>? Reveal { get; set; }

    /// <summary>Opens a folder; false when it could not.</summary>
    internal static Func<string, bool>? OpenFolderSeam { get; set; }

    /// <summary>Copies text to the clipboard; false when it could not.</summary>
    internal static Func<string, bool>? Copy { get; set; }

    /// <summary>Opens a web link (a Logs saved dialog button); false when it could not.</summary>
    internal static Func<string, bool>? OpenUrl { get; set; }

    /// <summary>Tests: replaces <see cref="SupportBundle.DiscordInviteUrl"/>.</summary>
    internal static Func<string>? DiscordUrl { get; set; }

    /// <summary>The folder bundles are saved to now (the Desktop, or <c>[Support] OutputFolder</c>), or null.</summary>
    internal static Func<string?>? Folder { get; set; }

    /// <summary><c>BepInEx/progenitor-support</c>, the fallback (the Last bundle line looks there too), or null.</summary>
    internal static Func<string?>? FallbackFolder { get; set; }

    /// <summary>The build itself (tests may replace it).</summary>
    internal static Func<SupportBundleRequest, SupportBundleResult> Builder { get; set; } = SupportBundle.Build;

    private static readonly object Gate = new();
    private static bool _running;
    private static string _status = "";

    /// <summary>The last collect's outcome line ("" before the first).</summary>
    internal static string Status
    {
        get { lock (Gate) return _status; }
    }

    internal static bool Running
    {
        get { lock (Gate) return _running; }
    }

    /// <summary>The last finished result, for the readback and tests.</summary>
    internal static SupportBundleResult? LastResult { get; private set; }

    /// <summary>
    /// <b>Collect logs</b>: gathers the request on this (main) thread and builds off it. Throws (the row shows the failure)
    /// when a collect is already running or this process cannot collect.
    /// </summary>
    internal static void Collect()
    {
        lock (Gate)
        {
            if (_running) throw new InvalidOperationException("Already collecting logs.");
            _running = true;
            _status = Collecting;
        }

        SupportBundleRequest? request;
        try
        {
            request = (Gather ?? DefaultGather())?.Invoke();
        }
        catch (Exception e)
        {
            string why = "Could not read the game's state: " + SupportRedactor.Scrub(e.Message);
            Fail(why);
            throw new InvalidOperationException(why);
        }
        if (request == null)
        {
            Fail("Collecting logs is unavailable here.");
            throw new InvalidOperationException("Collecting logs is unavailable here.");
        }

        void Work()
        {
            SupportBundleResult result;
            try { result = Builder(request); }
            catch (Exception e) { result = new SupportBundleResult { Ok = false, Error = SupportRedactor.Scrub(e.Message) }; }
            var post = PostMain ?? DefaultPostMain();
            if (post == null) Finish(result);
            else post(() => Finish(result));
        }

        // Returns the failure line, or null once the build is started.
        string? StartWork()
        {
            var off = RunOff ?? DefaultRunOff();
            if (off == null)
            {
                Work();
                return null;
            }
            try
            {
                off(Work);
                return null;
            }
            catch (Exception e)
            {
                string why = "Could not start: " + SupportRedactor.Scrub(e.Message);
                Fail(why);
                return why;
            }
        }

        // An admin of the joined Progenitor server: its own bundle first (the fetch ends exactly once, on the main thread),
        // then the client's build with it merged under server/. Without it, or when it fails, the client's bundle alone.
        bool wantServer = false;
        try { wantServer = (ServerLogsWanted ?? DefaultServerLogsWanted())?.Invoke() ?? false; }
        catch { wantServer = false; }
        var fetch = FetchServerLogs ?? DefaultFetchServerLogs();
        if (wantServer && fetch != null)
        {
            try
            {
                fetch(outcome =>
                {
                    // Progenitor is unloading: nothing may be started in this generation.
                    if (outcome.Unloading)
                    {
                        Fail("Collect logs stopped: Progenitor is unloading.");
                        return;
                    }
                    if (outcome.Ok && outcome.Zip != null)
                    {
                        // The SHA-256 is checked by the build, on the worker thread (SupportBundle.Build).
                        request.ServerZip = outcome.Zip;
                        request.ServerZipSha256 = outcome.ExpectedSha256;
                    }
                    else request.ServerPartNote = string.IsNullOrWhiteSpace(outcome.Reason) ? "unknown reason" : SupportRedactor.Scrub(outcome.Reason);
                    StartWork();
                });
                return;
            }
            catch (Exception e)
            {
                request.ServerPartNote = "the server could not be asked (" + SupportRedactor.Scrub(e.Message) + ")";
            }
        }

        string? failed = StartWork();
        if (failed != null) throw new InvalidOperationException(failed);
    }

    /// <summary>Main thread: whether Collect logs also asks the joined server for its logs (the setting is on, and this player is an admin of a Progenitor server).</summary>
    internal static Func<bool>? ServerLogsWanted { get; set; }

    /// <summary>Main thread: fetch the server's bundle; the callback runs once, on the main thread, with the zip or the reason.</summary>
    internal static Action<Action<ServerLogsOutcome>>? FetchServerLogs { get; set; }

    /// <summary>The fetch's progress line ("" when none runs).</summary>
    internal static Func<string>? ServerLogsProgress { get; set; }

    /// <summary>What the Last bundle line reads while collecting: the server fetch's progress, else <see cref="Collecting"/>.</summary>
    internal static string RunningLine
    {
        get
        {
            string p = "";
            try { p = (ServerLogsProgress ?? DefaultServerLogsProgress())?.Invoke() ?? ""; }
            catch { p = ""; }
            return p.Length > 0 ? p : Collecting;
        }
    }

    /// <summary>Main thread: reveal the zip, copy its path and write the status line.</summary>
    internal static void Finish(SupportBundleResult result)
    {
        LastResult = result;
        InvalidateNewest();
        string line;
        if (!result.Ok)
        {
            line = "Could not collect logs: " + (string.IsNullOrWhiteSpace(result.Error) ? "unknown error" : SupportRedactor.Scrub(result.Error));
        }
        else
        {
            bool revealed = false, copied = false;
            try { revealed = (Reveal ?? DefaultReveal())?.Invoke(result.Path) ?? false; }
            catch { /* fail soft */ }
            try { copied = (Copy ?? DefaultCopy())?.Invoke(result.Path) ?? false; }
            catch { /* fail soft */ }
            line = SuccessLine(result.FileName, result.Bytes, revealed, copied, SupportRedactor.Scrub(result.FolderLabel),
                result.FellBack ? SupportRedactor.Scrub(result.FallbackReason) : null);
            if (result.ServerPartIncluded) line += " The server's logs are inside, under server/.";
            else if (!string.IsNullOrWhiteSpace(result.ServerPartMissing))
                line += " Server logs not included: " + SupportRedactor.Scrub(result.ServerPartMissing) + ".";
            if (!string.IsNullOrWhiteSpace(result.LikelyInvolved)) line += " " + result.LikelyInvolved;
        }

        lock (Gate)
        {
            _running = false;
            _status = line;
        }

        // After the status line (so a page re-reading it sees the save): offer the Discord in a dialog. Nothing opens by
        // itself any more; the player picks a link or Not now.
        if (result.Ok) OfferDiscord(result);
    }

    // ---- the Logs saved dialog: offer the community Discord ------------------------------------------------------------

    internal const string OfferTitle = "Logs saved";
    internal const string JoinCaption = "Join the Discord";
    internal const string ChannelCaption = "Open #technical-support";
    internal const string NotNowCaption = "Not now";
    internal const string AskToOpenDiscordKey = "AskToOpenDiscord";
    internal const string AskToOpenDiscordLabel = "Offer to open the Discord after saving";
    internal const string AskToOpenDiscordHelp =
        "After Collect logs saves the zip, a dialog offers to open the V Rising Mod Community Discord (or its "
        + "#technical-support channel) where you post it. Off: only the status line says where the zip is.";

    /// <summary>The dialog's text and links; null links mean nothing to offer.</summary>
    internal sealed class DiscordOffer
    {
        internal string Title { get; init; } = OfferTitle;
        internal string Body { get; init; } = "";
        internal IReadOnlyList<(string Caption, string Url)> Links { get; init; } = Array.Empty<(string, string)>();
        internal string CancelCaption { get; init; } = NotNowCaption;
    }

    /// <summary>Tests: replaces <see cref="SupportBundle.TechSupportChannelUrl"/>.</summary>
    internal static Func<string>? ChannelUrl { get; set; }

    /// <summary><c>[Support] AskToOpenDiscord</c> (default on). Null in the game-free build: on.</summary>
    internal static Func<bool>? AskToOpenDiscord { get; set; }

    /// <summary>Main thread: why a dialog may not open now (a world loading or joining, the server), or null.</summary>
    internal static Func<string?>? DialogBlocked { get; set; }

    /// <summary>Shows the offer (the dialog kit's Choose). Null: no dialog can be shown here; the status line alone.</summary>
    internal static Action<DiscordOffer>? ShowOffer { get; set; }

    /// <summary>What the last save did about the offer: <c>shown</c>, <c>off</c>, <c>skipped: …</c> (readback, tests).</summary>
    internal static string LastOffer { get; private set; } = "";

    /// <summary>Only https links are opened.</summary>
    internal static bool IsOpenableUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Discord channel link (<c>https://discord.com/channels/…</c>), the only kind the channel button takes.</summary>
    internal static bool IsChannelUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url!.StartsWith("https://discord.com/channels/", StringComparison.OrdinalIgnoreCase)
        && url.Length > "https://discord.com/channels/".Length;

    /// <summary>
    /// The offer for a saved bundle: <c>&lt;file&gt; (&lt;size&gt;) is on your Desktop. Post it in …</c> (or <c>is in
    /// &lt;folder&gt;</c>), <b>Join the Discord</b> when the invite is an https link, <b>Open #technical-support</b> when the
    /// channel link is a Discord channel link, and <b>Not now</b>. Null when neither link is usable.
    /// </summary>
    internal static DiscordOffer? BuildOffer(string fileName, long bytes, string? folderLabel, bool fellBack, string? invite, string? channel)
    {
        var links = new List<(string, string)>();
        if (IsOpenableUrl(invite)) links.Add((JoinCaption, invite!));
        if (IsChannelUrl(channel)) links.Add((ChannelCaption, channel!));
        if (links.Count == 0) return null;
        string where = Ui.TitleLogsTile.Where(folderLabel, fellBack);
        string place = where == SupportBundle.DesktopLabel ? "on " + where : "in " + where;
        return new DiscordOffer
        {
            Body = fileName + " (" + SupportBundle.FormatBytes(bytes) + ") is " + place + ". Post it in the #technical-support "
                + "forum of the V Rising Mod Community Discord so the mod's developer can help.",
            Links = links,
        };
    }

    private static void OfferDiscord(SupportBundleResult result)
    {
        try
        {
            bool ask = true;
            try { ask = (AskToOpenDiscord ?? DefaultAskToOpenDiscord())?.Invoke() ?? true; }
            catch { ask = true; }
            if (!ask)
            {
                LastOffer = "off";
                return;
            }
            string? blocked = null;
            try { blocked = (DialogBlocked ?? DefaultDialogBlocked())?.Invoke(); }
            catch (Exception e) { blocked = "state unreadable (" + e.Message + ")"; }
            if (blocked != null)
            {
                LastOffer = "skipped: " + blocked;
                Plugin.Logger?.LogInfo("Support: the Logs saved dialog was skipped: " + blocked + ".");
                return;
            }
            var offer = BuildOffer(result.FileName, result.Bytes, SupportRedactor.Scrub(result.FolderLabel), result.FellBack,
                DiscordUrl?.Invoke() ?? SupportBundle.DiscordInviteUrl, ChannelUrl?.Invoke() ?? SupportBundle.TechSupportChannelUrl);
            if (offer == null)
            {
                LastOffer = "skipped: no link";
                return;
            }
            var show = ShowOffer ?? DefaultShowOffer();
            if (show == null)
            {
                LastOffer = "skipped: no dialog here";
                Plugin.Logger?.LogInfo("Support: no dialog can be shown here; the status line says where the zip is.");
                return;
            }
            show(offer);
            LastOffer = "shown";
        }
        catch (Exception e)
        {
            LastOffer = "failed: " + e.Message;
            Plugin.Logger?.LogWarning("Support: the Logs saved dialog could not be shown (" + e.Message + "); the status line says where the zip is.");
        }
    }

    /// <summary>
    /// The dialog's timing rule by the client's state name (<c>ClientBootstrapSystem.ClientState</c>): null at the title
    /// (<c>Idle</c>) and in game (<c>Running</c>), else why not (a world is loading or joining).
    /// </summary>
    internal static string? DialogBlockedFor(string? clientState) => clientState switch
    {
        null or "" or "Idle" or "Running" => null,
        _ => "a world is loading or joining (" + clientState + ")",
    };

    /// <summary>A link the dialog's button picked: https only, on the main thread (the dialog's answer runs there); one log line.</summary>
    internal static bool OpenOfferLink(string url)
    {
        if (!IsOpenableUrl(url))
        {
            Plugin.Logger?.LogWarning("Support: refused to open a link that is not https.");
            return false;
        }
        bool opened = false;
        try { opened = (OpenUrl ?? DefaultOpenUrl())?.Invoke(url) ?? false; }
        catch { opened = false; }
        Plugin.Logger?.LogInfo("Support: " + (opened ? "opened " : "could not open ") + url + ".");
        return opened;
    }

    private static void Fail(string line)
    {
        lock (Gate)
        {
            _running = false;
            _status = line;
        }
    }

    /// <summary>
    /// <c>Saved vrising-mod-logs-client-20261010-142233.zip (1.4 MB) and opened its folder; its path is on the clipboard.
    /// Drag it into your #technical-support post.</c>
    /// </summary>
    internal static string SuccessLine(string fileName, long bytes, bool revealed, bool copied, string? folderLabel = null,
        string? fallbackReason = null)
    {
        string label = string.IsNullOrWhiteSpace(folderLabel) ? "BepInEx/" + SupportBundle.FolderName : folderLabel!;
        var sb = new StringBuilder("Saved ").Append(fileName).Append(" (").Append(SupportBundle.FormatBytes(bytes)).Append(')');
        if (!string.IsNullOrWhiteSpace(fallbackReason))
            sb.Append(" in BepInEx/").Append(SupportBundle.FolderName).Append(" because ").Append(fallbackReason);
        else if (!revealed)
            sb.Append(label == SupportBundle.DesktopLabel ? " on " : " in ").Append(label);
        if (revealed) sb.Append(" and opened its folder");
        if (copied) sb.Append("; its path is on the clipboard");
        sb.Append(". Drag it into your #technical-support post.");
        return sb.ToString();
    }

    /// <summary>How long <see cref="NewestLine"/> keeps its answer: the Mods tab and Get help re-read the line every second.</summary>
    internal const long NewestCacheMs = 10_000;

    /// <summary>Milliseconds for the newest-bundle cache. Tests set it; null is the process clock.</summary>
    internal static Func<long>? ClockMs { get; set; }

    private static string _newestKey = "";
    private static string _newestLine = "";
    private static long _newestAt = long.MinValue;

    /// <summary>Forget the cached newest bundle (a collect finished; the next read lists the folders again).</summary>
    internal static void InvalidateNewest()
    {
        lock (Gate) _newestAt = long.MinValue;
    }

    /// <summary>
    /// The <b>Last bundle</b> line: name, size and local time of the newest bundle, or <see cref="NoneYet"/>. Listing the
    /// two folders (each for both bundle names) is cached for <see cref="NewestCacheMs"/>, keyed by the folders, so a
    /// changed <b>Save bundles in</b> setting reads again at once; a finished collect clears it.
    /// </summary>
    internal static string NewestLine()
    {
        string? folder = null, fallback = null;
        try { folder = (Folder ?? DefaultFolder())?.Invoke(); }
        catch { /* none */ }
        try { fallback = (FallbackFolder ?? DefaultFallbackFolder())?.Invoke(); }
        catch { /* none */ }
        string key = (folder ?? "") + "\n" + (fallback ?? "");
        long now = ClockMs?.Invoke() ?? Environment.TickCount64;
        lock (Gate)
        {
            if (_newestAt != long.MinValue && now - _newestAt < NewestCacheMs && now >= _newestAt
                && string.Equals(key, _newestKey, StringComparison.Ordinal))
                return _newestLine;
        }
        string line = ReadNewestLine(folder, fallback);
        lock (Gate)
        {
            _newestKey = key;
            _newestLine = line;
            _newestAt = now;
        }
        return line;
    }

    private static string ReadNewestLine(string? folder, string? fallback)
    {
        var newest = SupportBundle.Newest(folder);
        var other = SupportBundle.Newest(fallback);
        if (newest == null || (other != null && other.LastWriteTimeUtc > newest.LastWriteTimeUtc)) newest = other;
        if (newest == null) return NoneYet;
        return newest.Name + " · " + SupportBundle.FormatBytes(newest.Length) + " · "
            + newest.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <b>Open the logs folder</b>: the folder bundles are saved to when it exists, else <c>BepInEx/progenitor-support</c>
    /// (created when missing). The Desktop or a chosen folder is never created here. Throws when nothing can be opened.
    /// </summary>
    internal static void OpenFolder()
    {
        string? folder = (Folder ?? DefaultFolder())?.Invoke();
        string? fallback = (FallbackFolder ?? DefaultFallbackFolder())?.Invoke();
        bool usable = false;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try { usable = Directory.Exists(folder); }
            catch { usable = false; }
        }
        if (!usable)
        {
            folder = fallback;
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("The logs folder is unknown here.");
            Directory.CreateDirectory(folder!);
        }
        bool opened = (OpenFolderSeam ?? DefaultOpenFolder())?.Invoke(folder!) ?? false;
        if (!opened) throw new InvalidOperationException("Could not open the logs folder.");
    }

    internal static void ResetForTests()
    {
        Gather = null;
        RunOff = null;
        PostMain = null;
        Reveal = null;
        OpenFolderSeam = null;
        Copy = null;
        Folder = null;
        FallbackFolder = null;
        OpenUrl = null;
        DiscordUrl = null;
        ChannelUrl = null;
        AskToOpenDiscord = null;
        DialogBlocked = null;
        ShowOffer = null;
        LastOffer = "";
        ServerLogsWanted = null;
        ServerAdminProbe = null;
        FetchServerLogs = null;
        ServerLogsProgress = null;
        Builder = SupportBundle.Build;
        LastResult = null;
        ClockMs = null;
        InvalidateNewest();
        lock (Gate)
        {
            _running = false;
            _status = "";
        }
    }

#if PROGENITOR_TESTS
    private static Func<SupportBundleRequest?>? DefaultGather() => null;
    private static Func<Action, Task>? DefaultRunOff() => null;
    private static Action<Action>? DefaultPostMain() => null;
    private static Func<string, bool>? DefaultReveal() => null;
    private static Func<string, bool>? DefaultOpenFolder() => null;
    private static Func<string, bool>? DefaultCopy() => null;
    private static Func<string, bool>? DefaultOpenUrl() => null;
    private static Func<string?>? DefaultFolder() => null;
    private static Func<string?>? DefaultFallbackFolder() => null;
    private static Func<bool>? DefaultServerLogsWanted() => null;
    private static Action<Action<ServerLogsOutcome>>? DefaultFetchServerLogs() => null;
    private static Func<string>? DefaultServerLogsProgress() => null;
    private static Func<bool>? DefaultAskToOpenDiscord() => null;
    private static Func<string?>? DefaultDialogBlocked() => null;
    private static Action<DiscordOffer>? DefaultShowOffer() => null;
#else
    private static Func<SupportBundleRequest?>? DefaultGather() => SupportLive.GatherClient;
    private static Func<Action, Task>? DefaultRunOff() => work => Task.Run(work);
    private static Action<Action>? DefaultPostMain() => action => Dev.MainThread.Post(action);
    private static Func<string, bool>? DefaultReveal() => SupportLive.RevealInExplorer;
    private static Func<string, bool>? DefaultOpenFolder() => SupportLive.OpenFolderInExplorer;
    private static Func<string, bool>? DefaultCopy() => Ui.Ui.TryCopyToClipboard;
    private static Func<string, bool>? DefaultOpenUrl() => SupportLive.OpenUrl;
    private static Func<string?>? DefaultFolder() => SupportLive.OutputFolderNow;
    private static Func<string?>? DefaultFallbackFolder() => SupportLive.SupportFolder;
    private static Func<bool>? DefaultServerLogsWanted() => SupportLive.ServerLogsWanted;
    private static Action<Action<ServerLogsOutcome>>? DefaultFetchServerLogs() => ServerLogsLive.Fetch;
    private static Func<string>? DefaultServerLogsProgress() => ServerLogsLive.ProgressLine;
    private static Func<bool>? DefaultAskToOpenDiscord() => SupportLive.AskToOpenDiscord;
    private static Func<string?>? DefaultDialogBlocked() => SupportLive.DialogBlockedReason;
    private static Action<DiscordOffer>? DefaultShowOffer() => Ui.SupportOfferDialog.Show;
#endif
}
