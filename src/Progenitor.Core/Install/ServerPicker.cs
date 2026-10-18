using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Install;

/// <summary>INSTALLSIDES1: what one server-picker entry points the Install tab's Server column at.</summary>
internal enum ServerPickKind
{
    /// <summary>The server this client is connected to (the remote route, or the local folder when it is that server).</summary>
    Connected = 0,

    /// <summary>A dedicated-server folder on this PC (the local-folder route; works with the server stopped).</summary>
    LocalFolder = 1,

    /// <summary>The private games this client hosts.</summary>
    PrivateGames = 2,

    /// <summary>No server: the Server column has nothing to act on.</summary>
    None = 3,
}

/// <summary>INSTALLSIDES1: one server the picker offers.</summary>
internal sealed class ServerPickEntry
{
    public ServerPickEntry(
        string id, ServerPickKind kind, string caption, string shortName, string path = "", string detail = "",
        string choiceCaption = "", string address = "")
    {
        Id = id ?? "";
        Kind = kind;
        Caption = caption ?? "";
        ShortName = shortName ?? "";
        Path = path ?? "";
        Detail = detail ?? "";
        ChoiceCaption = string.IsNullOrEmpty(choiceCaption) ? Caption : choiceCaption;
        Address = address ?? "";
    }

    /// <summary>
    /// The caption in the picker's list: the toolbar caption, plus the connected server's address
    /// as a secondary part when it fits one button (<c>Connected: Forms Test · 127.0.0.1:9976</c>).
    /// </summary>
    public string ChoiceCaption { get; }

    /// <summary>The connected server's <c>address:port</c> (empty for the other kinds).</summary>
    public string Address { get; }

    /// <summary>Stable across rebuilds: <c>connected</c>, <c>local:&lt;canonical path&gt;</c>, <c>private</c>, <c>none</c>.</summary>
    public string Id { get; }

    public ServerPickKind Kind { get; }

    /// <summary>The picker's caption for it.</summary>
    public string Caption { get; }

    /// <summary>The name the Server column's header and the cells' words use.</summary>
    public string ShortName { get; }

    /// <summary>
    /// A local folder's canonical path; for the connected server, the folder on this PC that is that
    /// server (merged into the connected entry), else empty.
    /// </summary>
    public string Path { get; }

    /// <summary>Where it was found, for the readback (<c>configured</c>, <c>Steam</c>, <c>running now</c>…).</summary>
    public string Detail { get; }

    public ServerTargetKind TargetKind => Kind switch
    {
        ServerPickKind.Connected or ServerPickKind.LocalFolder => ServerTargetKind.Server,
        ServerPickKind.PrivateGames => ServerTargetKind.PrivateGames,
        _ => ServerTargetKind.None,
    };
}

/// <summary>INSTALLSIDES1: one dedicated-server folder found on this PC.</summary>
internal readonly struct LocalServerCandidate
{
    public LocalServerCandidate(string path, string name, string source, bool running = false, int port = 0)
    {
        Path = (path ?? "").Trim();
        Name = (name ?? "").Trim();
        Source = (source ?? "").Trim();
        Running = running;
        Port = port;
    }

    /// <summary>The game port in the folder's <c>ServerHostSettings.json</c>, or 0 when unknown.</summary>
    public int Port { get; }

    /// <summary>Canonical folder path.</summary>
    public string Path { get; }

    /// <summary>The server's own name (<c>ServerHostSettings.json</c>), or empty.</summary>
    public string Name { get; }

    /// <summary>Where it was found.</summary>
    public string Source { get; }

    public bool Running { get; }
}

/// <summary>INSTALLSIDES1: everything the picker's list is built from. Plain values.</summary>
internal sealed class ServerPickerInputs
{
    /// <summary>The client is in a game on a server.</summary>
    public bool Connected { get; init; }

    /// <summary>The connected server's identity (its name, else <c>address:port</c>).</summary>
    public ServerIdentity? ConnectedServer { get; init; }

    /// <summary>
    /// The validated <c>[Mods] DedicatedServerPath</c> (canonical), or empty. It is listed first among
    /// the local servers and is the default when nothing is connected.
    /// </summary>
    public string ConfiguredPath { get; init; } = "";

    /// <summary>The configured server's own name, or empty.</summary>
    public string ConfiguredName { get; init; } = "";

    /// <summary>The configured folder's game port, or 0 when unknown.</summary>
    public int ConfiguredPort { get; init; }

    /// <summary>
    /// The route selector proved the connected server is the configured folder's (port, this
    /// machine, handshake): the two are one server.
    /// </summary>
    public bool ConnectedIsConfigured { get; init; }

    /// <summary>The joined game port (Bootstrap's join record), or 0.</summary>
    public int ConnectedPort { get; init; }

    /// <summary>The joined address is this machine (loopback or one of its interfaces).</summary>
    public bool ConnectedIsThisMachine { get; init; }

    /// <summary>
    /// The name the connected server's signed catalog gives itself, or empty. The catalog's default
    /// (<see cref="ManifestDefaultName"/>) names no server and is ignored.
    /// </summary>
    public string ConnectedCatalogName { get; init; } = "";

    /// <summary>Other dedicated-server folders found on this PC (the existing Steam / registry / running discovery).</summary>
    public IReadOnlyList<LocalServerCandidate> Discovered { get; init; } = Array.Empty<LocalServerCandidate>();

    /// <summary>F35: the private game this client hosts (its server folder), or empty. It is the connected server.</summary>
    public string HostedFolder { get; init; } = "";

    /// <summary>
    /// LOCALDETECT1: the local server folder auto-detected as the connected server (it owns the joined port on this PC),
    /// or empty. It is the connected server, under its own name.
    /// </summary>
    public string DetectedFolder { get; init; } = "";

    /// <summary>LOCALDETECT1: the detected server's own name, or empty.</summary>
    public string DetectedName { get; init; } = "";

    /// <summary>LOCALDETECT1: the detected server's game port, or 0.</summary>
    public int DetectedPort { get; init; }
}

/// <summary>
/// INSTALLSIDES1: the server picker at the top of the Install tab. Entries, in order: the connected
/// server (when connected), every dedicated-server folder on this PC (the configured one first),
/// <c>Private games (this PC)</c>, and <c>None · this client only</c>. With nothing picked — or a pick
/// that is no longer offered — the default is the connected server, else the configured folder,
/// else the private games this client hosts. Game-free; the caller supplies the facts.
/// </summary>
internal static class ServerPicker
{
    public const string IdConnected = "connected";
    public const string IdPrivate = "private";
    public const string IdNone = "none";
    public const string LocalPrefix = "local:";

    public const string ConnectedPrefix = "Connected: ";
    public const string ThisPcSuffix = " (this PC)";
    public const string PrivateGamesCaption = "Private games (this PC)";
    public const string PrivateGamesName = "Private games";
    public const string NoneCaption = "None · this client only";
    public const string LocalUnnamed = "Dedicated server";

    /// <summary>The picker's label and the Server column's header word.</summary>
    public const string ServerWord = "Server";

    /// <summary>The picker dialog's title.</summary>
    public const string DialogTitle = "Act on which server?";

    /// <summary>The picker dialog's one-line body.</summary>
    public const string DialogBody = "The Server column and its buttons act on the server you pick.";

    public const string SourceConfigured = "configured";

    /// <summary>F35 (live 2026-10-08): the private game this client hosts, in the Server column's header and the picker.</summary>
    public const string PrivateGameName = "Your private game";

    public const string SourceHosted = "hosted";

    /// <summary>LOCALDETECT1: a local server folder auto-detected as the connected server.</summary>
    public const string SourceDetected = "detected";

    public static string LocalId(string path) => LocalPrefix + (path ?? "").Trim();

    /// <summary>The list, in the picker's order, with duplicates (the same folder twice) dropped.</summary>
    public static IReadOnlyList<ServerPickEntry> Build(ServerPickerInputs inputs)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        var list = new List<ServerPickEntry>(4 + inputs.Discovered.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inputs.Connected)
        {
            var server = inputs.ConnectedServer ?? ServerIdentity.Connected("", "");
            var same = ConnectedFolder(inputs);
            string name = ConnectedName(inputs);
            if (name.Length == 0) name = server.ShortName;
            string address = server.Address;
            // The folder on this PC that is the connected server is the same server: it is offered
            // once, as the connected entry, under the folder's name.
            if (same is { } folder) seen.Add(Normalize(folder.Path));
            string caption = ConnectedPrefix + name;
            string choice = address.Length > 0 && !string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
                && caption.Length + 3 + address.Length <= ChoiceCaptionMax
                ? caption + " · " + address
                : caption;
            list.Add(new ServerPickEntry(IdConnected, ServerPickKind.Connected, caption, name,
                path: same?.Path ?? "",
                detail: same != null ? address + (address.Length > 0 ? " · " : "") + SameServerDetail : address,
                choiceCaption: choice, address: address));
        }

        string configured = (inputs.ConfiguredPath ?? "").Trim();
        // Review H1: the live dedicated server is never offered (discovery lists running servers too).
        if (configured.Length > 0 && !LocalServerFolder.IsLiveServerFolder(configured) && seen.Add(Normalize(configured)))
            list.Add(Local(configured, inputs.ConfiguredName, SourceConfigured));

        for (int i = 0; i < inputs.Discovered.Count; i++)
        {
            var found = inputs.Discovered[i];
            if (found.Path.Length == 0 || LocalServerFolder.IsLiveServerFolder(found.Path) || !seen.Add(Normalize(found.Path))) continue;
            list.Add(Local(found.Path, found.Name, found.Running ? found.Source + " · running" : found.Source));
        }

        list.Add(new ServerPickEntry(IdPrivate, ServerPickKind.PrivateGames, PrivateGamesCaption, PrivateGamesName));
        list.Add(new ServerPickEntry(IdNone, ServerPickKind.None, NoneCaption, ""));
        return list;
    }

    /// <summary>The longest list caption that keeps the address beside the name on one popup button.</summary>
    public const int ChoiceCaptionMax = 44;

    /// <summary>The connected entry's readback detail when a folder on this PC is that server.</summary>
    public const string SameServerDetail = "same server as a folder on this PC";

    /// <summary>The name a signed catalog carries when its operator set none (ModManifestJson's default).</summary>
    public const string ManifestDefaultName = "V Rising";

    /// <summary>
    /// The dedicated-server folder on this PC that is the connected server, or null: the configured
    /// folder when the route selector proved it, else the one folder whose game port is the joined
    /// port when the joined address is this machine (two such folders: no guess).
    /// </summary>
    public static LocalServerCandidate? ConnectedFolder(ServerPickerInputs inputs)
    {
        if (inputs == null || !inputs.Connected) return null;
        // F35: the private game this client hosts is the connected server, under its own name.
        string hosted = (inputs.HostedFolder ?? "").Trim();
        if (hosted.Length > 0) return new LocalServerCandidate(hosted, PrivateGameName, SourceHosted);
        // LOCALDETECT1: the folder whose own server holds the joined port (the route selector took it).
        string detected = (inputs.DetectedFolder ?? "").Trim();
        if (detected.Length > 0 && !LocalServerFolder.IsLiveServerFolder(detected))
            return new LocalServerCandidate(detected, inputs.DetectedName ?? "", SourceDetected, running: true, port: inputs.DetectedPort);
        string configured = (inputs.ConfiguredPath ?? "").Trim();
        if (inputs.ConnectedIsConfigured && configured.Length > 0)
            return new LocalServerCandidate(configured, inputs.ConfiguredName, SourceConfigured, port: inputs.ConfiguredPort);
        if (!inputs.ConnectedIsThisMachine || inputs.ConnectedPort <= 0) return null;
        LocalServerCandidate? match = null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(LocalServerCandidate c)
        {
            if (c.Path.Length == 0 || c.Port != inputs.ConnectedPort || !paths.Add(Normalize(c.Path))) return;
            match = paths.Count == 1 ? c : null;
        }

        if (configured.Length > 0) Consider(new LocalServerCandidate(configured, inputs.ConfiguredName, SourceConfigured, port: inputs.ConfiguredPort));
        for (int i = 0; i < inputs.Discovered.Count; i++) Consider(inputs.Discovered[i]);
        return paths.Count == 1 ? match : null;
    }

    /// <summary>
    /// The connected server's name, as the install confirmations name a server: the name of the
    /// folder on this PC that is that server (its <c>ServerHostSettings.json</c> / <c>-serverName</c>),
    /// else the name its signed catalog gives (unless the catalog's default), else empty (the caller
    /// falls back to the address).
    /// </summary>
    public static string ConnectedName(ServerPickerInputs inputs)
    {
        if (inputs == null || !inputs.Connected) return "";
        if ((inputs.HostedFolder ?? "").Trim().Length > 0) return PrivateGameName;
        var known = inputs.ConnectedServer;
        if (known != null && known.HasName) return known.Name;
        if (ConnectedFolder(inputs) is { } folder)
        {
            string name = folder.Name.Length > 0 ? folder.Name : FolderName(folder.Path);
            if (name.Length > 0) return name;
        }

        string catalog = (inputs.ConnectedCatalogName ?? "").Trim();
        return catalog.Length > 0 && !string.Equals(catalog, ManifestDefaultName, StringComparison.OrdinalIgnoreCase) ? catalog : "";
    }

    private static ServerPickEntry Local(string path, string name, string source)
    {
        string shortName = (name ?? "").Trim();
        if (shortName.Length == 0) shortName = FolderName(path);
        if (shortName.Length == 0) shortName = LocalUnnamed;
        return new ServerPickEntry(LocalId(path), ServerPickKind.LocalFolder, shortName + ThisPcSuffix, shortName, path, source);
    }

    private static string FolderName(string path)
    {
        try
        {
            string trimmed = (path ?? "").TrimEnd('\\', '/');
            return trimmed.Length == 0 ? "" : System.IO.Path.GetFileName(trimmed);
        }
        catch
        {
            return "";
        }
    }

    private static string Normalize(string path) => (path ?? "").Trim().TrimEnd('\\', '/');

    /// <summary>
    /// The entry in effect: <paramref name="pickedId"/> when the list still offers it, else the
    /// default — the connected server, else the configured folder, else the private games.
    /// </summary>
    public static ServerPickEntry Resolve(string? pickedId, IReadOnlyList<ServerPickEntry> entries)
    {
        if (entries == null || entries.Count == 0)
            return new ServerPickEntry(IdPrivate, ServerPickKind.PrivateGames, PrivateGamesCaption, PrivateGamesName);
        string id = (pickedId ?? "").Trim();
        if (id.Length > 0)
        {
            for (int i = 0; i < entries.Count; i++)
                if (SameId(entries[i].Id, id)) return entries[i];
            // A folder picked earlier that is now the connected server: the merged connected entry.
            if (id.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = id.Substring(LocalPrefix.Length);
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Kind == ServerPickKind.Connected && entries[i].Path.Length > 0 && SameId(entries[i].Path, path))
                        return entries[i];
            }
        }

        return Default(entries);
    }

    /// <summary>The default: connected, else the first local folder (the configured one is listed first), else private games.</summary>
    public static ServerPickEntry Default(IReadOnlyList<ServerPickEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == ServerPickKind.Connected) return entries[i];
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == ServerPickKind.LocalFolder && entries[i].Detail == SourceConfigured) return entries[i];
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == ServerPickKind.PrivateGames) return entries[i];
        return entries[0];
    }

    /// <summary>Ids compare case-insensitively (a local id carries a Windows path).</summary>
    public static bool SameId(string? a, string? b) =>
        string.Equals((a ?? "").Trim().TrimEnd('\\', '/'), (b ?? "").Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The picker dialog's line about the connected server, naming it and giving its address
    /// (<c>Connected to Progenitor Forms Test at 127.0.0.1:9976, a server on this PC.</c>); empty when
    /// not connected.
    /// </summary>
    public static string ConnectedLine(IReadOnlyList<ServerPickEntry> entries)
    {
        if (entries == null) return "";
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Kind != ServerPickKind.Connected) continue;
            string at = e.Address.Length > 0 && !string.Equals(e.ShortName, e.Address, StringComparison.OrdinalIgnoreCase)
                ? " at " + e.Address
                : "";
            return "Connected to " + e.ShortName + at + (e.Path.Length > 0 ? ", a server on this PC." : ".");
        }

        return "";
    }

    /// <summary>The Server column's header: <c>Server · Forms Test</c>, <c>Server · Private games</c>, or <c>Server</c>.</summary>
    public static string HeaderCaption(ServerPickEntry? entry)
    {
        string name = entry?.ShortName ?? "";
        return name.Length == 0 ? ServerWord : ServerWord + " · " + name;
    }

    public static string KindName(ServerPickKind kind) => kind switch
    {
        ServerPickKind.Connected => "connected",
        ServerPickKind.LocalFolder => "local",
        ServerPickKind.PrivateGames => "private-games",
        _ => "none",
    };

    /// <summary>
    /// The route the Server column acts by for the picked entry. The connected server keeps the
    /// route the selector decided (which is the local folder when the connected server is that
    /// folder's); a local folder is the local route when it is the validated configured folder;
    /// the private games and None act on no server.
    /// </summary>
    public static ServerRoute RouteFor(ServerPickEntry entry, ServerRoute connectedRoute, string configuredPath)
    {
        if (entry == null) return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        switch (entry.Kind)
        {
            case ServerPickKind.Connected:
                return connectedRoute;
            case ServerPickKind.LocalFolder:
                if (connectedRoute.ForcedRemote && connectedRoute.Block == ServerRouteBlock.Disconnected)
                    return connectedRoute;
                return (configuredPath ?? "").Trim().Length > 0 && SameId(entry.Path, configuredPath)
                    ? new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None)
                    : new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
            default:
                return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        }
    }

    /// <summary>Readback of one entry.</summary>
    public static Dictionary<string, object?> ToWidget(ServerPickEntry entry) => new()
    {
        ["id"] = entry.Id,
        ["kind"] = KindName(entry.Kind),
        ["caption"] = entry.Caption,
        ["name"] = entry.ShortName,
        ["path"] = entry.Path,
        ["detail"] = entry.Detail,
        ["address"] = entry.Address,
        ["choice"] = entry.ChoiceCaption,
    };
}
