using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Reload;

namespace Progenitor.Commands;

/// <summary>
/// Framework commands that ship with Core. A plugin that also claims <c>.help</c> does not
/// replace them: both short routes disable and the qualified routes stay. They return when
/// that plugin unloads.
/// </summary>
internal static class BuiltInCommands
{
    [Command("help", shortHand: "h", usage: ".help [command]", description: "List commands you can use, or show usage for one command.")]
    public static void Help(ICommandContext ctx, [Remainder] string query = "")
    {
        try
        {
            query = NormalizeQuery(query);
            if (string.IsNullOrEmpty(query))
            {
                ListAll(ctx);
                return;
            }

            if (TryShowOne(ctx, query)) return;

            ctx.Reply($"Unknown command '{query}'. Type {CommandRegistry.Prefix}help for a list.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: .help failed: {e}");
            ctx.Reply("Help failed.");
        }
    }

    [Command("progenitor", shortHand: "prog", description: "Print the Progenitor version and loaded reloadable plugins.")]
    public static void ProgenitorInfo(ICommandContext ctx)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("Progenitor ").Append(Plugin.Version);
            sb.Append(" (").Append(Plugin.IsServerProcess ? "server" : "client").Append(')');
            ctx.Reply(sb.ToString());

            List<string> names;
            try { names = PluginReloader.LoadedPluginNames(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"Commands: LoadedPluginNames failed: {e.Message}");
                ctx.Reply("Reloadable plugins: (unavailable)");
                return;
            }

            if (names.Count == 0)
            {
                ctx.Reply("Reloadable plugins: none");
                return;
            }

            ctx.Reply("Reloadable plugins: " + string.Join(", ", names));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: .progenitor failed: {e}");
            ctx.Reply("Command failed.");
        }
    }

    /// <summary>The one subcommand of <c>.progenitor</c>.</summary>
    internal const string LogsSubcommand = "logs";

    /// <summary>
    /// SUPPORT1: <c>.progenitor logs</c> builds this server's support bundle (BepInEx/progenitor-support, the same zip the
    /// client's Collect logs makes) and replies with its name and folder relative to the server's folder. Admin only, by the
    /// server's admin-list rule (<see cref="AdminListGateAttribute"/>), so <c>.help</c> shows it only to admins. The file
    /// work runs off the main thread.
    /// </summary>
    [AdminListGate]
    [Command("progenitor", shortHand: "prog", adminOnly: false, usage: ".progenitor logs",
        description: "Admin only. Save this server's logs as one .zip for a help post (BepInEx/progenitor-support).")]
    public static void ProgenitorLogs(ICommandContext ctx, string what)
    {
        try
        {
            if (!CommandAccess.ListedAdmin(ctx))
            {
                ctx.Reply("This command is admin-only.");
                return;
            }
            if (!string.Equals((what ?? "").Trim(), LogsSubcommand, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Reply("Usage: " + CommandRegistry.Prefix + "progenitor logs");
                return;
            }
            Progenitor.Support.SupportLive.CollectForCommand(ctx);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: .progenitor logs failed: {e}");
            ctx.Reply("Command failed.");
        }
    }

    /// <summary>
    /// SERVERINST3: the server half of Progenitor's graceful "Stop server". Admin only. Saves the
    /// world through <see cref="Progenitor.Install.ServerStopCommand.SaveWorld"/> and quits only
    /// after that save succeeds; a failed save replies with the reason and the server keeps
    /// running. Nothing here inspects the caller's machine or stops any other process.
    ///
    /// <para>SERVERINST4: this is the <b>second</b> entry to that one path, kept for an admin who
    /// would rather type it. The pane's <c>Stop server</c> no longer submits this text: it sends a
    /// <c>StopServerRequest</c> on Progenitor's own reserved event id and gets a reply it can name
    /// (see <see cref="Progenitor.Install.StopServerEvents"/>). Both entries reach the same
    /// <see cref="Progenitor.Install.ServerStopCommand.Execute(bool, System.Action{string})"/>, and
    /// the admin check is the same value on both: <see cref="ICommandContext.IsAdmin"/> here,
    /// <c>User.IsAdmin</c> re-read off the sender entity there.</para>
    /// </summary>
    // Review (L3): the same rule as the stop channel and remote installs (admin list, adminauth, or the host), checked
    // here rather than by adminOnly, which follows only the game's admin flag.
    [Command(Progenitor.Install.ServerProcessControl.StopCommandName, adminOnly: false, usage: ".stop-server", description: "Save the world and stop this server (admin).")]
    public static void StopServer(ICommandContext ctx)
    {
        try
        {
            bool admin = Progenitor.Install.ServerAdminList.AuthorizeShared(ctx.User.PlatformId, ctx.IsAdmin) != Progenitor.Install.RemoteAuthorizedBy.None;
            Progenitor.Install.ServerStopCommand.Execute(admin, ctx.Reply);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: .stop-server failed: {e}");
            ctx.Reply("Stop failed.");
        }
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        query = query.Trim();
        string prefix = CommandRegistry.Prefix;
        if (prefix.Length > 0 && query.StartsWith(prefix, StringComparison.Ordinal))
            query = query.Substring(prefix.Length).TrimStart();
        return query;
    }

    private static void ListAll(ICommandContext ctx)
    {
        var all = CommandRegistry.ListActive();
        var lines = new List<string>();
        foreach (var bundle in all)
        {
            if (!Visible(ctx, bundle)) continue;
            lines.Add("  " + CommandRegistry.FormatHelpLine(bundle));
        }

        if (lines.Count == 0)
        {
            ctx.Reply("No commands available.");
            return;
        }

        ctx.Reply("Available commands:");
        foreach (var line in lines) ctx.Reply(line);
    }

    private static bool TryShowOne(ICommandContext ctx, string query)
    {
        var tokens = CommandParser.Tokenize(query);
        if (tokens.Count == 0) return false;

        if (string.Equals(tokens[0], CommandRegistry.FallbackRoot, StringComparison.OrdinalIgnoreCase)
            && tokens.Count >= 3
            && CommandRegistry.TryGetQualifiedOwner(tokens[1], out var byPath))
        {
            string path = tokens.Count >= 4 ? tokens[2] + " " + tokens[3] : tokens[2];
            if (byPath.TryGetValue(path, out var qualified) && Visible(ctx, qualified))
            {
                ShowBundle(ctx, qualified);
                return true;
            }
            if (tokens.Count == 3)
            {
                var ownedGroup = CommandRegistry.ListGroup(tokens[2]);
                var visibleOwned = new List<CommandBundle>();
                foreach (var b in ownedGroup)
                {
                    if (string.Equals(b.OwnerId, tokens[1], StringComparison.OrdinalIgnoreCase)
                        && Visible(ctx, b))
                        visibleOwned.Add(b);
                }
                if (visibleOwned.Count > 0)
                {
                    ctx.Reply($"{CommandRegistry.Prefix}{CommandRegistry.FallbackRoot} {visibleOwned[0].OwnerId} {visibleOwned[0].GroupName} commands:");
                    foreach (var b in visibleOwned)
                        ctx.Reply("  " + CommandRegistry.FormatHelpLine(b));
                    return true;
                }
            }
        }

        if (tokens.Count >= 2 && CommandRegistry.TryGetGroupedSlot(tokens[0], tokens[1], out var grouped))
        {
            if (grouped.Ambiguous)
            {
                ctx.Reply(CommandRegistry.FormatConflictReply(tokens[0] + " " + tokens[1], grouped));
                return true;
            }
            if (grouped.Bundle != null && Visible(ctx, grouped.Bundle))
            {
                ShowBundle(ctx, grouped.Bundle);
                return true;
            }
        }

        if (CommandRegistry.TryGetRootSlot(tokens[0], out var root))
        {
            if (root.Ambiguous)
            {
                ctx.Reply(CommandRegistry.FormatConflictReply(tokens[0], root));
                return true;
            }
            if (root.Bundle != null && Visible(ctx, root.Bundle))
            {
                ShowBundle(ctx, root.Bundle);
                return true;
            }
        }

        var group = CommandRegistry.ListGroup(tokens[0]);
        if (group.Count > 0)
        {
            var visible = new List<CommandBundle>();
            foreach (var b in group)
                if (Visible(ctx, b)) visible.Add(b);
            if (visible.Count == 0) return false;

            ctx.Reply($"{CommandRegistry.Prefix}{visible[0].GroupName} commands:");
            foreach (var b in visible)
                ctx.Reply("  " + CommandRegistry.FormatHelpLine(b));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The dispatcher's own test: some overload this caller may run, counting the server's
    /// admin-list rule for a command gated by it (<see cref="AdminListGateAttribute"/>).
    /// </summary>
    private static bool Visible(ICommandContext ctx, CommandBundle bundle) =>
        bundle.VisibleTo(ctx.IsAdmin, CommandAccess.ListedAdminFor(ctx, bundle));

    private static void ShowBundle(ICommandContext ctx, CommandBundle bundle)
    {
        bool listedAdmin = CommandAccess.ListedAdminFor(ctx, bundle);
        foreach (var overload in bundle.Overloads)
        {
            if (!overload.UsableBy(ctx.IsAdmin, listedAdmin)) continue;
            ctx.Reply(CommandRegistry.FormatUsage(bundle, overload));
            ctx.Reply("  fallback " + CommandRegistry.FormatQualified(bundle));
            if (!string.IsNullOrEmpty(overload.Attribute.Description))
                ctx.Reply("  " + overload.Attribute.Description);
        }
    }
}
