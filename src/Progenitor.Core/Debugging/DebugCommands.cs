using System;
using Progenitor.Commands;
#if !PROGENITOR_TESTS
using Progenitor.Players;
using Progenitor.WorldState;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Debugging;

/// <summary>
/// Core's <c>.debug</c> command: the debug buff on, off or read back for yourself or another
/// online player, the map teleport's server path, and the server-wide build mode
/// (<c>.debug build on|off</c>, normally reached through <see cref="DebugBuildCommands"/>).
/// Every form authorizes with the server's admin-list rule (decided 2026-10-09,
/// <see cref="Authorized"/>): on the admin list, else authenticated with <c>adminauth</c>, else
/// the host of this private game. Not <c>adminOnly</c>, which follows only the game's admin flag
/// and would refuse a listed admin who has not run <c>adminauth</c> since the restart; marked
/// <see cref="AdminListGateAttribute"/> instead, so the dispatcher refuses everyone else with its
/// own words (<c>This command is admin-only.</c>) before any argument is bound and <c>.help</c>
/// hides it from them, and the body checks the rule again before it parses. Registered by
/// <see cref="DebugTools"/> on the server under Core's owner. The body compiles into the test
/// project with the game half left out, so the gate and the parse are driven through the real
/// dispatcher there.
/// </summary>
internal static class DebugCommands
{
    /// <summary>The command name. Short and generic (standing rule): <c>.debug</c>.</summary>
    internal const string Name = "debug";

    /// <summary>The refusal for a caller the server's admin rule does not authorize; same words as the dispatcher's.</summary>
    internal const string AdminOnlyReply = "This command is admin-only.";

    internal const string Description =
        "Admin only. Debug buff: no blood drain, invulnerable, immortal, sun and hazard immune, ignored by foes, "
        + "survives death, map revealed and hold-to-teleport on the map. Bare .debug reads back what is applied; "
        + "name an online player to act on them. .debug build on|off: server-wide sandbox build mode for every player "
        + "(no costs, free placement, everything unlocked; pieces still join the castle); off restores the settings it changed. "
        + ".debug build detached lists pieces that belong to no castle.";

    [AdminListGate]
    [Command(Name, adminOnly: false, usage: DebugCommandParse.Usage, description: Description)]
    public static void Debug(ICommandContext ctx, string what = "", [Remainder] string rest = "")
    {
        // The admin rule first, so a player learns nothing from the parse.
        if (!Authorized(ctx))
        {
            ctx.Reply(AdminOnlyReply);
            return;
        }
        if (!DebugCommandParse.TryParse(what, rest, out var request, out var error))
        {
            ctx.Reply(error);
            return;
        }

#if PROGENITOR_TESTS
        LastRequest = request;
#else
        try
        {
            Execute(ctx, request);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Debug: .debug threw: " + e);
            ctx.Reply("Debug failed. See the server log.");
        }
#endif
    }

    /// <summary>
    /// The server's one admin rule for every <c>.debug</c> form (<c>ServerAdminList.AuthorizeShared</c>,
    /// through <see cref="CommandAccess.ListedAdmin"/>): on the admin list, authenticated with
    /// <c>adminauth</c>, or the host. The same call in tests, where the SteamID and the game's admin
    /// flag come from the test context.
    /// </summary>
    internal static bool Authorized(ICommandContext ctx) => CommandAccess.ListedAdmin(ctx);

#if PROGENITOR_TESTS
    /// <summary>Tests only: the request the last admitted <c>.debug</c> parsed to.</summary>
    internal static DebugRequest? LastRequest;
#else
    private static void Execute(ICommandContext ctx, DebugRequest request)
    {
        var world = ctx.World;
        if (request.Action == DebugAction.Teleport)
        {
            DebugMap.RunCommand(world, ctx.UserEntity, ctx.Reply, request.X, request.Z);
            return;
        }

        if (request.IsBuild)
        {
            ExecuteBuild(ctx, request.Action, request.Piece, request.Confirm);
            return;
        }

        var kind = DebugCommandParse.KindOf(request.Action);
        if (!request.TargetsOther)
        {
            DebugBuff.Run(new DebugBuff.Target(world, ctx.UserEntity, ctx.CharacterEntity, ctx.Reply), kind);
            if (kind == DebugBuff.Kind.Status) ReplyBuildMode(ctx);
            return;
        }

        if (!TryResolveOnline(ctx, request.Player, out var user, out var character, out var name)) return;
        ctx.Reply(DebugCommandParse.FormatTargetHeader(name, request.Action));
        DebugBuff.Run(new DebugBuff.Target(world, user, character, ctx.Reply), kind);
        if (kind == DebugBuff.Kind.Status) ReplyBuildMode(ctx);

        string notice = DebugCommandParse.FormatTargetNotice(request.Action);
        if (notice.Length > 0 && user != ctx.UserEntity)
        {
            try { Progenitor.Chat.Chat.SendTo(world, user, notice); }
            catch (Exception e) { DebugBuff.Log("target notice threw: " + e.Message); }
        }
    }

    /// <summary>
    /// <c>.debug build on|off|status</c>, from either entry. The server's one admin rule, as the map
    /// teleport uses (<c>ServerAdminList.AuthorizeShared</c>): on the admin list, authenticated with
    /// <c>adminauth</c>, or the host of this private game.
    /// </summary>
    internal static void ExecuteBuild(ICommandContext ctx, DebugAction action, string piece = "", int confirm = -1)
    {
        var by = Progenitor.Install.ServerAdminList.AuthorizeShared(ctx.User.PlatformId, ctx.User.IsAdmin);
        if (by == Progenitor.Install.RemoteAuthorizedBy.None)
        {
            ctx.Reply(AdminOnlyReply);
            return;
        }
        string who = ctx.User.CharacterName.ToString() + " (" + Progenitor.Install.ServerAdminList.Word(by) + ")";
        if (action is DebugAction.BuildDetached or DebugAction.BuildDetachedRemove or DebugAction.BuildDetachedRemoveAll)
        {
            DetachedPiecesRuntime.Run(ctx.World, ctx.UserEntity, action, piece, confirm, who, ctx.Reply);
            return;
        }
        DebugBuildRuntime.Run(ctx.World, action, who, ctx.Reply);
    }

    /// <summary>The one build-mode line under a status readout (server-wide, so the same for every player).</summary>
    private static void ReplyBuildMode(ICommandContext ctx)
    {
        try
        {
            ctx.Reply(DebugBuildRuntime.State() switch
            {
                DebugBuildMode.RecordState.Present => "Build mode: on (server-wide). .debug build for details.",
                DebugBuildMode.RecordState.Absent => "Build mode: off. .debug build for details.",
                _ => "Build mode: unknown. .debug build for details.",
            });
        }
        catch (Exception e) { DebugBuff.Log("build mode readout threw: " + e.Message); }
    }

    /// <summary>
    /// Another online player by display name, through the player directory, as live entities of
    /// the current world epoch. The session refs are re-checked against the live <c>User</c> row
    /// so a stale directory snapshot can never name somebody else's entity.
    /// </summary>
    private static bool TryResolveOnline(ICommandContext ctx, string player, out Entity user, out Entity character, out string name)
    {
        user = Entity.Null;
        character = Entity.Null;
        name = player;
        var found = PlayerDirectory.FindByName(player, onlineOnly: true, world: ctx.World);
        if (found.Status == PlayerLookupStatus.Ambiguous)
        {
            ctx.Reply(DebugCommandParse.FormatAmbiguousPlayer(player, found.Candidates.Count));
            return false;
        }
        if (found.Status != PlayerLookupStatus.Exact || found.Player == null)
        {
            ctx.Reply(DebugCommandParse.FormatNoSuchPlayer(player));
            return false;
        }

        var info = found.Player;
        name = info.DisplayName;
        if (info.WorldEpoch != WorldEpoch.Current)
        {
            ctx.Reply("The player list is from an earlier world load; try again in a moment.");
            return false;
        }

        var em = ctx.World.EntityManager;
        var session = info.Session;
        var candidate = new Entity { Index = session.UserIndex, Version = session.UserVersion };
        if (candidate == Entity.Null || !em.Exists(candidate) || !em.HasComponent<User>(candidate))
        {
            ctx.Reply(DebugCommandParse.FormatNoSuchPlayer(player));
            return false;
        }

        var row = em.GetComponentData<User>(candidate);
        if (!row.IsConnected || row.PlatformId != info.PlatformId)
        {
            ctx.Reply(DebugCommandParse.FormatNoSuchPlayer(player));
            return false;
        }

        user = candidate;
        character = row.LocalCharacter._Entity;
        if (character != Entity.Null && !em.Exists(character)) character = Entity.Null;
        return true;
    }
#endif
}

/// <summary>
/// <c>.debug build [on|off|status]</c> as its own literal subcommand. The dispatcher matches
/// <c>&lt;group&gt; &lt;member&gt;</c> before the root <c>.debug</c>, so every other <c>.debug</c>
/// form resolves to the root. Gated like the root: not <c>adminOnly</c> (the dispatcher's admin
/// flag follows only <c>User.IsAdmin</c> or the host, and a listed admin who has not run
/// <c>adminauth</c> since the restart would be refused) but <see cref="AdminListGateAttribute"/>,
/// so the dispatcher and the body both check the server's admin-list rule, everyone else gets the
/// dispatcher's own words, and <c>.help</c> hides the line from them.
/// </summary>
[CommandGroup(DebugCommands.Name)]
internal static class DebugBuildCommands
{
    /// <summary>The member token: <c>.debug build</c>.</summary>
    internal const string Member = "build";

    internal const string Description =
        "Admin only. Server-wide sandbox build mode for every player: no build costs, free placement, no castle limits, "
        + "everything unlocked, drag building; pieces still join the castle of the territory they stand in. "
        + "Off restores exactly the settings it changed; a server restart turns it off (expected; not yet verified). "
        + "detached lists pieces that belong to no castle (built under an earlier build mode); detached remove removes yours.";

    [AdminListGate]
    [Command(Member, adminOnly: false, usage: DebugCommandParse.BuildUsage, description: Description)]
    public static void Build(ICommandContext ctx, [Remainder] string what = "")
    {
        // The admin rule first, so a player learns nothing from the parse.
        if (!DebugCommands.Authorized(ctx))
        {
            ctx.Reply(DebugCommands.AdminOnlyReply);
            return;
        }
        if (!DebugCommandParse.TryParse(Member, what, out var request, out var error))
        {
            ctx.Reply(error);
            return;
        }
#if PROGENITOR_TESTS
        DebugCommands.LastRequest = request;
#else
        try
        {
            DebugCommands.ExecuteBuild(ctx, request.Action, request.Piece, request.Confirm);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Debug: .debug build threw: " + e);
            ctx.Reply("Debug build failed. See the server log.");
        }
#endif
    }
}
