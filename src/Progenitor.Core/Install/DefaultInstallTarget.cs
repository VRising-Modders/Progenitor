using System;

namespace Progenitor.Install;

/// <summary>
/// INSTALLUX1 item 2: the Configuration row <c>Default install target</c>
/// (<c>[Install] DefaultTarget</c>). The Install tab has no per-row target chooser any more — the
/// decision was "it installs to the location based on their settings" — so this one setting
/// answers "where does Install put it?" for every row.
///
/// <para>REMOTEINST1 (decided 2026-10-01): there is one server target, <b>Server</b>. Which
/// server it reaches — the configured local folder, or the connected Progenitor server through an
/// admin's remote install — is the route (<see cref="ServerRouteSelector"/>), never a second choice.
/// The old <c>localServer</c> value is still read and is rewritten to <c>server</c>.</para>
/// </summary>
internal enum DefaultInstallTargetSetting
{
    /// <summary>
    /// The default. The listing's own side decides: a Server mod goes to the server, a Client mod to
    /// the client, a Both mod to both. Every server-naming answer falls back to the client when no
    /// server route exists, because installing to the client already covers the private games this
    /// player hosts.
    /// </summary>
    FollowSide = 0,

    /// <summary>Always both halves (client only when there is no server route).</summary>
    Both = 1,

    /// <summary>Always the client (and the private games hosted from it).</summary>
    Client = 2,

    /// <summary>Always the server (the client when there is no server route). Was <c>localServer</c>.</summary>
    Server = 3,
}

/// <summary>The resolved target and whether a server-naming choice had to fall back to the client.</summary>
internal readonly struct ResolvedInstallTarget
{
    public ResolvedInstallTarget(InstallTarget target, bool fellBackToClient)
        : this(target, fellBackToClient, default, false, true)
    {
    }

    public ResolvedInstallTarget(
        InstallTarget target,
        bool fellBackToClient,
        ServerRoute route,
        bool toConnectedServer,
        bool localInstall)
    {
        Target = target;
        FellBackToClient = fellBackToClient;
        Route = route;
        ToConnectedServer = toConnectedServer;
        LocalInstall = localInstall;
    }

    /// <summary>
    /// What this process writes locally: <see cref="InstallTarget.Client"/>,
    /// <see cref="InstallTarget.LocalServer"/> (the local folder route) or
    /// <see cref="InstallTarget.Both"/>. Meaningless when <see cref="LocalInstall"/> is false.
    /// </summary>
    public InstallTarget Target { get; }

    /// <summary>
    /// True when the setting (or the listing's side) named the server and no route could take it, so
    /// the install went to the client instead. The confirmation says why
    /// (<see cref="ServerRoute.Reason"/>).
    /// </summary>
    public bool FellBackToClient { get; }

    /// <summary>The server route this resolution was made against.</summary>
    public ServerRoute Route { get; }

    /// <summary>REMOTEINST1: the server half goes to the connected server as a remote install.</summary>
    public bool ToConnectedServer { get; }

    /// <summary>False only for a remote-only install: nothing is written on this machine.</summary>
    public bool LocalInstall { get; }
}

/// <summary>
/// Parsing, captions and the resolution rule for <c>[Install] DefaultTarget</c>. Pure: no config
/// file, no folder probe — the caller passes the route.
/// </summary>
internal static class DefaultInstallTarget
{
    public const string FollowSideValue = "followSide";
    public const string BothValue = "both";
    public const string ClientValue = "client";
    public const string ServerValue = "server";

    /// <summary>The value every build before REMOTEINST1 wrote for the server target. Read, then migrated.</summary>
    public const string LegacyLocalServerValue = "localServer";

    /// <summary>The caption earlier builds showed for it; still accepted by <see cref="Parse"/>.</summary>
    public const string LegacyLocalServerCaption = "Local server";

    // UI polish (requested 2026-10-01: "I don't understand what follow the mod's side is"): captions
    // only; the stored values are unchanged.
    public const string FollowSideCaption = "Automatic (by mod type)";
    public const string BothCaption = "Client and server";
    public const string ClientCaption = "My client only";
    public const string ServerCaption = "Server only";

    /// <summary>Captions earlier builds showed; still read by <see cref="Parse"/>.</summary>
    internal static readonly string[] LegacyCaptions = { "Follow the mod's side", "Both", "My client", "Server" };

    /// <summary>The body sentence a confirmation adds when a server target fell back to the client with no server at all.</summary>
    public const string NoLocalServerSentence = ServerRouteCopy.NoServer;

    public const DefaultInstallTargetSetting Default = DefaultInstallTargetSetting.FollowSide;

    /// <summary>The four values, in the order the Configuration row lists them.</summary>
    public static readonly DefaultInstallTargetSetting[] All =
    {
        DefaultInstallTargetSetting.FollowSide,
        DefaultInstallTargetSetting.Both,
        DefaultInstallTargetSetting.Client,
        DefaultInstallTargetSetting.Server,
    };

    /// <summary>The stored value of one setting (the config file's string).</summary>
    public static string ValueOf(DefaultInstallTargetSetting setting) => setting switch
    {
        DefaultInstallTargetSetting.Both => BothValue,
        DefaultInstallTargetSetting.Client => ClientValue,
        DefaultInstallTargetSetting.Server => ServerValue,
        _ => FollowSideValue,
    };

    /// <summary>The player-facing caption of one setting.</summary>
    public static string CaptionOf(DefaultInstallTargetSetting setting) => setting switch
    {
        DefaultInstallTargetSetting.Both => BothCaption,
        DefaultInstallTargetSetting.Client => ClientCaption,
        DefaultInstallTargetSetting.Server => ServerCaption,
        _ => FollowSideCaption,
    };

    /// <summary>A stored value or a caption back to a setting; anything unknown (or empty) is the default.</summary>
    public static DefaultInstallTargetSetting Parse(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return Default;
        if (IsLegacy(t)) return DefaultInstallTargetSetting.Server;
        for (int i = 0; i < All.Length; i++)
        {
            var candidate = All[i];
            if (string.Equals(t, ValueOf(candidate), StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, CaptionOf(candidate), StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, LegacyCaptions[i], StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return Default;
    }

    /// <summary>True for the pre-REMOTEINST1 spelling of the server target.</summary>
    public static bool IsLegacy(string? text)
    {
        string t = (text ?? "").Trim();
        return string.Equals(t, LegacyLocalServerValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, LegacyLocalServerCaption, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The config migration: a stored legacy value is rewritten to <see cref="ServerValue"/>. Returns
    /// true (and the value to write) only when the stored text needs rewriting; every current value,
    /// and anything unknown, is left exactly as the user wrote it.
    /// </summary>
    public static bool TryMigrate(string? stored, out string migrated)
    {
        migrated = stored ?? "";
        if (!IsLegacy(stored)) return false;
        migrated = ServerValue;
        return true;
    }

    /// <summary>
    /// The pre-REMOTEINST1 rule, kept for callers that only know whether a local server folder is
    /// configured: a configured folder is the local route, none is no route.
    /// </summary>
    public static ResolvedInstallTarget Resolve(
        DefaultInstallTargetSetting setting, PackageSide side, bool serverConfigured) =>
        Resolve(setting, side, serverConfigured
            ? new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None)
            : new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer));

    /// <summary>
    /// The target one Install click writes to, given the server route. The local route keeps the
    /// local-folder behavior exactly. The remote route sends the server half to the connected server
    /// (a Server mod writes nothing locally; Both writes the client half locally as well). No route —
    /// or a remote route that cannot install right now — falls back to the client, and the route's
    /// reason says why.
    /// </summary>
    public static ResolvedInstallTarget Resolve(
        DefaultInstallTargetSetting setting, PackageSide side, ServerRoute route)
    {
        InstallTarget wanted = setting switch
        {
            DefaultInstallTargetSetting.Both => InstallTarget.Both,
            DefaultInstallTargetSetting.Client => InstallTarget.Client,
            DefaultInstallTargetSetting.Server => InstallTarget.LocalServer,
            _ => side switch
            {
                PackageSide.Server => InstallTarget.LocalServer,
                PackageSide.Both => InstallTarget.Both,
                // Client, and Unknown: the client is the half this process can always write.
                _ => InstallTarget.Client,
            },
        };

        if (wanted == InstallTarget.Client) return new ResolvedInstallTarget(InstallTarget.Client, false, route, false, true);
        if (route.Kind == ServerRouteKind.Local) return new ResolvedInstallTarget(wanted, false, route, false, true);
        if (route.Kind == ServerRouteKind.Remote && route.CanInstall)
            return new ResolvedInstallTarget(InstallTarget.Client, false, route, toConnectedServer: true,
                localInstall: wanted == InstallTarget.Both);
        return new ResolvedInstallTarget(InstallTarget.Client, true, route, false, true);
    }
}

/// <summary>
/// The Configuration description of <c>Default install target</c> for the option on show: what it
/// does, one short line per choice, what "server" means, and a live "Right now:" line from the
/// actual route (UI polish, 2026-10-01). Pure: the caller passes the route and the folder.
/// </summary>
internal static class DefaultInstallTargetHelp
{
    public const string AutomaticLine = "Automatic: each mod goes where it runs — client mods to this game, server mods to the server, mods for both to both.";
    public const string BothLine = "Client and server: every mod installs to both this game and the server.";
    public const string ClientLine = "My client only: mods install to this game only, never to a server. Server mods still load in private games you host.";
    public const string ServerLine = "Server only: mods install to the server only.";
    public const string ServerMeaning =
        "\"Server\" means your local server folder when you're not connected, or connected to it; otherwise the server you're connected to, if you're on its admin list.";

    public static string Lead(DefaultInstallTargetSetting setting) => setting switch
    {
        DefaultInstallTargetSetting.Both => "Every mod installs to both this game and the server.",
        DefaultInstallTargetSetting.Client => "Mods install to this game only, never to a server. Server mods still load in private games you host.",
        DefaultInstallTargetSetting.Server => "Mods install to the server only.",
        _ => "Each mod goes where it runs: client mods to this game, server mods to the server, mods for both to both.",
    };

    /// <summary>Where the server half goes now, in words.</summary>
    public static string ServerNow(ServerRoute route, string? localPath, RemoteAuthorizedBy authorizedBy) => route.Kind switch
    {
        ServerRouteKind.Local => "your local server" + (string.IsNullOrWhiteSpace(localPath) ? "" : " at " + localPath!.Trim()),
        ServerRouteKind.Remote => "the server you're connected to ("
            + (authorizedBy == RemoteAuthorizedBy.AdminAuth ? "you're an admin there" : "you're on its admin list") + ")",
        _ => "",
    };

    /// <summary>The live line for the option on show.</summary>
    public static string RightNow(DefaultInstallTargetSetting setting, ServerRoute route, string? localPath, RemoteAuthorizedBy authorizedBy)
    {
        bool server = route.Kind != ServerRouteKind.None && route.CanInstall;
        string where = ServerNow(route, localPath, authorizedBy);
        string why = route.Block is ServerRouteBlock.None or ServerRouteBlock.NoServer ? "" : " (" + route.Reason.TrimEnd('.') + ")";
        switch (setting)
        {
            case DefaultInstallTargetSetting.Client:
                return "Right now: mods install to this game.";
            case DefaultInstallTargetSetting.Both:
                return server
                    ? "Right now: mods go to this game and " + where + "."
                    : "Right now: no server is available" + why + ", so mods install to this game only.";
            case DefaultInstallTargetSetting.Server:
                return server
                    ? "Right now: mods go to " + where + "."
                    : "Right now: not available — no server to install to" + why + ", so mods install to this game instead.";
            default:
                return server
                    ? "Right now: server mods go to " + where + "."
                    : "Right now: no server is available" + why + ", so server mods install to this game only. They still load in private games you host.";
        }
    }

    /// <summary>The whole description body.</summary>
    public static string Describe(DefaultInstallTargetSetting setting, ServerRoute route, string? localPath, RemoteAuthorizedBy authorizedBy) =>
        Lead(setting) + "\n\n" + RightNow(setting, route, localPath, authorizedBy)
        + "\n\n" + AutomaticLine + "\n" + BothLine + "\n" + ClientLine + "\n" + ServerLine
        + "\n\n" + ServerMeaning;
}

/// <summary>
/// Reads <c>[Install] DefaultTarget</c> off Progenitor's own config file, binding it on the first
/// read exactly as <c>LocalIndexConfig</c> does for <c>[Install] LocalIndexPath</c>. A test build
/// (or a process with no Progenitor plugin instance) gets the default and writes nothing.
/// </summary>
internal static class DefaultInstallTargetConfig
{
    public static DefaultInstallTargetSetting Resolve()
    {
        string? value = Config.ConfigHelper.DefaultInstallTargetValue;
        if (value != null)
        {
            MigrateBound(value);
            return DefaultInstallTarget.Parse(value);
        }
#if !PROGENITOR_TESTS
        var file = TrySharedConfigFile();
        if (file == null) return DefaultInstallTarget.Default;
        try
        {
            string stored = Config.ConfigHelper.BindDefaultInstallTarget(file).Value;
            MigrateBound(stored);
            return DefaultInstallTarget.Parse(stored);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: binding [" + Config.ConfigHelper.InstallSection + "] "
                                      + Config.ConfigHelper.DefaultInstallTargetKey + " threw (" + e.Message + "); using the default.");
            return DefaultInstallTarget.Default;
        }
#else
        return DefaultInstallTarget.Default;
#endif
    }

    /// <summary>
    /// REMOTEINST1: rewrite a legacy <c>localServer</c> in the bound entry to <c>server</c>. The value
    /// it stands for does not change, so a failed write costs nothing but the rewrite.
    /// </summary>
    private static void MigrateBound(string? stored)
    {
        if (!DefaultInstallTarget.TryMigrate(stored, out string migrated)) return;
        var entry = Config.ConfigHelper.DefaultInstallTargetEntry;
        if (entry == null) return;
        try
        {
            entry.Value = migrated;
#if !PROGENITOR_TESTS
            Plugin.Logger?.LogInfo("Install: [" + Config.ConfigHelper.InstallSection + "] " + Config.ConfigHelper.DefaultInstallTargetKey
                                   + " '" + stored + "' is now '" + migrated + "' (one Server target).");
#endif
        }
        catch
        {
            // Parse still reads the legacy value as Server.
        }
    }

#if !PROGENITOR_TESTS
    private static BepInEx.Configuration.ConfigFile? TrySharedConfigFile() =>
        Progenitor.Config.SharedConfig.TryGetOrLog("[" + Config.ConfigHelper.InstallSection + "] " + Config.ConfigHelper.DefaultInstallTargetKey);
#endif
}
