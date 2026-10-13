using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>SRVUNINST1: what removes the client's copy of a mod from the Mods page.</summary>
internal enum ClientRemover
{
    /// <summary>Nothing: no client copy, or one Progenitor cannot tell is its own.</summary>
    None = 0,

    /// <summary>The install ledger, through the uninstall seam (marked for the next start, or unloaded now).</summary>
    Seam = 1,

    /// <summary>FWALLOW1: the one DLL in <c>BepInEx/progenitor-reload</c> that declares it, deleted now.</summary>
    Reload = 2,
}

/// <summary>SRVUNINST1: what removes the server's copy of a mod from the Mods page.</summary>
internal enum ServerRemover
{
    None = 0,

    /// <summary>The connected server's admin uninstall (the Install tab's remote request).</summary>
    Remote = 1,

    /// <summary>The configured local server folder's ledger, through the same seam as the client copy.</summary>
    LocalSeam = 2,
}

/// <summary>SRVUNINST1: the evidence the plan is decided from. Plain values, so every branch has a test.</summary>
internal sealed class ModUninstallFacts
{
    internal string Name { get; init; } = "";

    // ---- the client copy ---------------------------------------------------------------------

    internal bool ClientPresent { get; init; }

    /// <summary>F1: this client's install record names the mod, but no catalog listing maps its plugin id yet.</summary>
    internal bool ClientLedgeredUnresolved { get; init; }

    /// <summary>The remover the caller found for the client copy; <see cref="ClientRemover.None"/> when none.</summary>
    internal ClientRemover Client { get; init; }

    /// <summary>The seam's copy is the reloader's own, so it unloads now.</summary>
    internal bool ClientHot { get; init; }

    internal string ReloadFile { get; init; } = "";
    internal IReadOnlyList<string> AlsoRemoves { get; init; } = Array.Empty<string>();

    // ---- the server copy ---------------------------------------------------------------------

    internal bool ServerPresent { get; init; }

    /// <summary>The server route the Install tab uses now (REMOTEINST1).</summary>
    internal ServerRoute Route { get; init; } = new(ServerRouteKind.None, ServerRouteBlock.NoServer);

    /// <summary>The connected server's ledger row for this mod's listing, or null.</summary>
    internal RemoteServerRow? LedgerRow { get; init; }

    /// <summary>The connected server's row for this plugin GUID (<see cref="RemotePluginRef"/>), or null.</summary>
    internal RemoteServerRow? PluginRow { get; init; }

    /// <summary>Local route only: the ledger names a copy on the local server folder that the seam resolves.</summary>
    internal bool LocalServerOurs { get; init; }

    /// <summary>F10: that copy goes now: the local server's reloader holds a trusted hot-unloadable image (the server's state).</summary>
    internal bool LocalServerHot { get; init; }

    /// <summary>The server the server copy is on, for the words that name it.</summary>
    internal ServerIdentity? Server { get; init; }

    /// <summary>The listing says the mod runs on the server only.</summary>
    internal bool ServerOnlyMod { get; init; }

    /// <summary>
    /// The server refused this mod's last uninstall for its dependency check and allows the override
    /// (<see cref="RemoteUninstallForce.Offers"/>): the next one is sent with force, and the confirmation says so.
    /// </summary>
    internal RemoteInstallProgress? LastRefusal { get; init; }
}

/// <summary>
/// SRVUNINST1: what the Mods page's Uninstall does for one mod, per side — decided before the
/// confirmation opens, so the dialog says exactly what will happen and a mod nothing can remove has
/// no confirmation at all, only the reason. Each side is decided as the Install tab decides it: the
/// client copy by its own remover, the server copy by the route (the connected server's admin
/// uninstall, or the configured local folder).
/// </summary>
internal sealed class ModUninstallPlan
{
    // ---- words --------------------------------------------------------------------------------

    internal const string ClientNotOurs = CatalogUninstallSeam.NotOurs;

    /// <summary>F1: a ledgered mod whose listing is not in the catalog yet (never "not ours").</summary>
    internal const string ClientLedgerNotMapped =
        "Progenitor installed this mod, but its listing is not loaded yet; open Install Mods once, then uninstall it here.";

    internal static string ServerNotOursLocal(string server) =>
        "Progenitor did not install the copy on " + server + ", so it cannot remove it from here.";

    internal static string ServerUnreported(string server) =>
        server + " did not say which files are this mod's, so its copy cannot be removed from here.";

    internal static string ServerManagedElsewhere(string server) =>
        "Managed elsewhere on " + server + ": it cannot tell which files are this mod's.";

    internal static string ServerAlreadyPending(string server) =>
        "Already removed from " + server + " at its next restart.";

    internal const string ServerNotConnected = "Join the server as an admin to remove its copy.";
    internal const string ServerNotProgenitor = "The server doesn't run Progenitor, so its copy cannot be removed from here.";
    internal const string ServerChecking = "Checking what this server allows; try again in a moment.";
    internal const string PlayersUnaffected = " Players without it are not affected.";

    /// <summary>The body sentence for the server's copy, from what its row says the server will do.</summary>
    internal static string ServerSentence(string server, RemoteRowState state, bool byPlugin) => state switch
    {
        RemoteRowState.Hot => "Removed from " + server + " now; its reloader unloads it, nothing to restart.",
        RemoteRowState.Pending => "The copy waiting on " + server + " is deleted now; nothing loads there at its next restart.",
        _ => "Removed from " + server + " at its next restart (or now, if it was hot loaded there).",
    };

    internal static string LocalServerSentence(string server, bool also, bool now = false) =>
        (also ? "Also removed from " : "Removed from ") + server
        + (now ? " now; its reloader unloads it, nothing to restart." : " at its next restart.");

    // ---- the decision -------------------------------------------------------------------------

    internal ClientRemover Client { get; private init; }
    internal ServerRemover Server { get; private init; }

    /// <summary>What the remote request names: a Thunderstore key, or <c>plugin:&lt;guid&gt;</c>.</summary>
    internal string RemotePackage { get; private init; } = "";

    internal RemoteRowState RemoteState { get; private init; }
    internal bool RemoteByPlugin { get; private init; }

    /// <summary>The client copy is unloaded now (else marked for the next start), as the seam decides it.</summary>
    internal bool ClientUnloadsNow { get; private init; }

    /// <summary>F10: the local server folder's copy goes now (that server's reloader holds it), not at its next restart.</summary>
    internal bool ServerUnloadsNow { get; private init; }

    /// <summary>The remote request skips the server's dependency check (after a refusal that allows it).</summary>
    internal bool RemoteForce { get; private init; }

    internal string ClientWhyNot { get; private init; } = "";
    internal string ServerWhyNot { get; private init; } = "";

    /// <summary>The server's name as the page's sentences use it.</summary>
    internal string ServerName { get; private init; } = "";

    /// <summary>The server the server copy is on (for the result note's words).</summary>
    internal ServerIdentity? Identity { get; private init; }

    internal bool CanAct => Client != ClientRemover.None || Server != ServerRemover.None;

    /// <summary>REQRULE6: the heading's <c>from …</c> words: this game, the server, or both, as the removers act.</summary>
    internal string TargetWords => CanAct
        ? InstallModsCopy.TargetWords(Client != ClientRemover.None, Server != ServerRemover.None, Identity?.TitleName ?? ServerName)
        : "";

    /// <summary>The disabled button's reason: each side's own, client first. Empty while the plan can act.</summary>
    internal string WhyNot
    {
        get
        {
            if (CanAct) return "";
            string why = (ClientWhyNot + " " + ServerWhyNot).Trim();
            return why.Length > 0 ? why : InstallModsCopy.RemoveFailed;
        }
    }

    /// <summary>The confirmation's body: one sentence per side that is removed, then what stays.</summary>
    internal string Body { get; private set; } = "";

    internal static ModUninstallPlan Decide(ModUninstallFacts facts)
    {
        facts ??= new ModUninstallFacts();
        var server = facts.Server ?? (facts.Route.Kind == ServerRouteKind.Local
            ? ServerIdentity.Local("", "")
            : ServerIdentity.Connected("", ""));
        string name = server.ShortName;

        var client = facts.ClientPresent ? facts.Client : ClientRemover.None;
        string clientWhy = facts.ClientPresent && client == ClientRemover.None
            ? (facts.ClientLedgeredUnresolved ? ClientLedgerNotMapped : ClientNotOurs)
            : "";

        var side = DecideServer(facts, name);
        var plan = new ModUninstallPlan
        {
            Client = client,
            ClientWhyNot = clientWhy,
            Server = side.Remover,
            ServerWhyNot = side.Why,
            RemotePackage = side.Package,
            RemoteState = side.State,
            RemoteByPlugin = side.ByPlugin,
            ClientUnloadsNow = client == ClientRemover.Seam && facts.ClientHot,
            ServerUnloadsNow = side.Remover == ServerRemover.LocalSeam && facts.LocalServerHot,
            RemoteForce = side.Remover == ServerRemover.Remote && RemoteUninstallForce.Offers(facts.LastRefusal),
            ServerName = name,
            Identity = server,
        };
        plan.Body = plan.CanAct ? Compose(plan, facts, name) : "";
        return plan;
    }

    private static (ServerRemover Remover, string Why, string Package, RemoteRowState State, bool ByPlugin) DecideServer(
        ModUninstallFacts facts, string name)
    {
        if (!facts.ServerPresent) return (ServerRemover.None, "", "", RemoteRowState.None, false);
        var route = facts.Route;
        switch (route.Kind)
        {
            case ServerRouteKind.Local:
                return facts.LocalServerOurs
                    ? (ServerRemover.LocalSeam, "", "", RemoteRowState.None, false)
                    : (ServerRemover.None, ServerNotOursLocal(name), "", RemoteRowState.None, false);

            case ServerRouteKind.Remote:
            {
                // The ledger row first (the Install tab's own row), then the server's plugin row.
                bool byPlugin = facts.LedgerRow == null;
                var row = facts.LedgerRow ?? facts.PluginRow;
                if (row == null) return (ServerRemover.None, ServerUnreported(name), "", RemoteRowState.None, false);
                return row.State switch
                {
                    RemoteRowState.UninstallPending => (ServerRemover.None, ServerAlreadyPending(name), "", row.State, byPlugin),
                    RemoteRowState.None => (ServerRemover.None, ServerManagedElsewhere(name), "", row.State, byPlugin),
                    _ => (ServerRemover.Remote, "", row.Package, row.State, byPlugin),
                };
            }

            default:
                string why = route.Block switch
                {
                    ServerRouteBlock.NotAdmin or ServerRouteBlock.NotAllowed or ServerRouteBlock.RemoteOff => route.Reason + ".",
                    ServerRouteBlock.NotProgenitor => ServerNotProgenitor,
                    ServerRouteBlock.Pending => ServerChecking,
                    _ => ServerNotConnected,
                };
                return (ServerRemover.None, why, "", RemoteRowState.None, false);
        }
    }

    private static string Compose(ModUninstallPlan plan, ModUninstallFacts facts, string name)
    {
        var parts = new List<string>(3);
        switch (plan.Client)
        {
            case ClientRemover.Seam:
                parts.Add(facts.ClientHot ? ModPageCopy.UninstallBodyHot : ModPageCopy.UninstallBody);
                break;
            case ClientRemover.Reload:
                parts.Add(ModPageCopy.UninstallBodyReload(System.IO.Path.GetFileName(facts.ReloadFile), facts.AlsoRemoves));
                break;
        }

        switch (plan.Server)
        {
            case ServerRemover.Remote:
                string sentence = ServerSentence(name, plan.RemoteState, plan.RemoteByPlugin);
                if (plan.RemoteForce) sentence += " " + RemoteUninstallForce.ConfirmBody(facts.LastRefusal);
                if (!facts.ClientPresent && facts.ServerOnlyMod) sentence += PlayersUnaffected;
                parts.Add(sentence);
                break;
            case ServerRemover.LocalSeam:
                parts.Add(LocalServerSentence(name, also: parts.Count > 0, now: plan.ServerUnloadsNow));
                break;
        }

        // What stays, so the confirmation never implies a copy goes that does not.
        if (facts.ClientPresent && plan.Client == ClientRemover.None)
            parts.Add("Your client's copy stays: " + Lower(plan.ClientWhyNot.Length > 0 ? plan.ClientWhyNot : ClientNotOurs));
        if (facts.ServerPresent && plan.Server == ServerRemover.None && plan.ServerWhyNot.Length > 0)
            parts.Add("The copy on " + name + " stays. " + plan.ServerWhyNot);
        return string.Join(" ", parts);
    }

    private static string Lower(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);

    /// <summary>
    /// The page's sentence for the server's answer to its uninstall: the step while it runs, what was
    /// done when it finished, and the server's own reason when it refused or failed.
    /// </summary>
    internal static string ResultNote(RemoteInstallProgress? progress, ServerIdentity? server)
    {
        if (progress == null) return "";
        string name = server?.ShortName ?? ServerIdentity.RemoteFallback;
        if (progress.Stage is RemoteInstallStage.Refused or RemoteInstallStage.Failed)
        {
            string why = ServerRouteCopy.Failure(progress).Trim().TrimEnd('.');
            if (why.Length == 0) why = InstallStatusLabels.Failure(progress);
            return Capital(name) + " did not remove it: " + Lower(why) + "."
                + (RemoteUninstallForce.Offers(progress) ? " Uninstall again to remove it without that check." : "");
        }

        string stage = ServerRouteCopy.Stage(progress, server).Trim();
        if (stage.Length == 0) return "";
        return stage.EndsWith("…", StringComparison.Ordinal) || stage.EndsWith(".", StringComparison.Ordinal) ? stage : stage + ".";
    }

    private static string Capital(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence.Substring(1);

    /// <summary>The page's note when Yes finds a different plan than the box showed: nothing is sent.</summary>
    internal const string ChangedNote = "Changed since the box opened, so nothing was done. Check the page and try again.";

    /// <summary>Would the two plans do exactly the same thing, in the same words?</summary>
    internal static bool SameAction(ModUninstallPlan? a, ModUninstallPlan? b) =>
        a != null && b != null
        && a.Client == b.Client && a.Server == b.Server
        && string.Equals(a.RemotePackage, b.RemotePackage, StringComparison.OrdinalIgnoreCase)
        && a.RemoteState == b.RemoteState && a.RemoteForce == b.RemoteForce
        && string.Equals(a.Body, b.Body, StringComparison.Ordinal);

    /// <summary>The readback of the plan (<c>page-uninstall</c>).</summary>
    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["canAct"] = CanAct,
        ["client"] = Client switch { ClientRemover.Seam => "seam", ClientRemover.Reload => "reload", _ => "none" },
        ["server"] = Server switch { ServerRemover.Remote => "remote", ServerRemover.LocalSeam => "local", _ => "none" },
        ["remotePackage"] = RemotePackage,
        ["remoteState"] = RemoteState.ToString(),
        ["remoteForce"] = RemoteForce,
        ["serverName"] = ServerName,
        ["why"] = WhyNot,
        ["clientWhyNot"] = ClientWhyNot,
        ["serverWhyNot"] = ServerWhyNot,
        ["serverUnloadsNow"] = ServerUnloadsNow,
        ["body"] = Body,
    };
}
