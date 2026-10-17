using System;
using System.Collections.Generic;
using System.Reflection;
#if !PROGENITOR_TESTS
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Commands;

/// <summary>
/// Resolves a chat line against <see cref="CommandRegistry"/> and invokes the winning overload.
/// The chat hook and the debug bridge both call <see cref="TryExecute"/> so a command can be
/// exercised without going through <c>ChatMessageSystem</c>.
/// </summary>
public static class CommandDispatcher
{
#if !PROGENITOR_TESTS
    /// <summary>
    /// Attempts to run <paramref name="text"/> as the user at <paramref name="userEntity"/>.
    /// Character is taken from <see cref="User.LocalCharacter"/> because the caller did not
    /// supply one (the debug bridge). The chat hook uses the four-argument overload with
    /// <c>ChatMessage.CharacterEntity</c>. Returns <see cref="CommandResult.NotACommand"/> on
    /// the client process so callers can share one path.
    /// </summary>
    public static CommandResult TryExecute(World world, Entity userEntity, string text) =>
        TryExecute(world, userEntity, Entity.Null, text);

    /// <summary>
    /// Same as <see cref="TryExecute(World, Entity, string)"/> but uses an explicit character
    /// entity. The chat hook passes <c>ChatMessage.CharacterEntity</c> (FromCharacter.Character).
    /// When that is <see cref="Entity.Null"/>, character falls back to
    /// <see cref="User.LocalCharacter"/>.
    /// </summary>
    public static CommandResult TryExecute(World world, Entity userEntity, Entity characterEntity, string text)
    {
        try
        {
            return ExecuteCore(world, userEntity, characterEntity, text);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: TryExecute threw: {e}");
            return CommandResult.Error;
        }
    }
#endif

    /// <summary>
    /// Game-free dispatch: prefix, route resolution, bind, permission, invoke. Both the short
    /// route and the qualified fallback reach this method.
    /// </summary>
    public static CommandResult TryDispatch(ICommandContext ctx, string text)
    {
        try
        {
            return DispatchCore(ctx, text);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: TryDispatch threw: {e}");
            return CommandResult.Error;
        }
    }

#if !PROGENITOR_TESTS
    private static CommandResult ExecuteCore(World world, Entity userEntity, Entity characterEntity, string text)
    {
        if (!Plugin.IsServerProcess) return CommandResult.NotACommand;
        if (world == null || !world.IsCreated) return CommandResult.Error;
        if (text == null) return CommandResult.NotACommand;

        string prefix = CommandRegistry.Prefix;
        if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.Ordinal))
            return CommandResult.NotACommand;

        var em = world.EntityManager;
        if (!em.HasComponent(userEntity, ComponentType.ReadOnly<User>()))
        {
            Plugin.Logger?.LogWarning("Commands: TryExecute was given an entity without User.");
            return CommandResult.Error;
        }

        var user = em.GetComponentData<User>(userEntity);
        if (characterEntity == Entity.Null)
            characterEntity = user.LocalCharacter._Entity;

        var ctx = new CommandContext(world, userEntity, characterEntity, user, text);
        return DispatchCore(ctx, text);
    }
#endif

    private static CommandResult DispatchCore(ICommandContext ctx, string text)
    {
        if (text == null) return CommandResult.NotACommand;

        CommandRegistry.EnsureBuiltIns();

        string prefix = CommandRegistry.Prefix;
        if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.Ordinal))
            return CommandResult.NotACommand;

        string rest = text.Substring(prefix.Length);
        var tokens = CommandParser.TokenizeDetailed(rest);
        if (tokens.Count == 0) return CommandResult.Unmatched;

        if (!TryResolve(tokens, ctx, rest, out var bundle, out var argTokens, out var argsRaw, out var resolveResult))
            return resolveResult;

        return Invoke(ctx, bundle, argTokens, argsRaw);
    }

    private static bool TryResolve(
        List<CommandParser.ArgToken> tokens,
        ICommandContext ctx,
        string rest,
        out CommandBundle bundle,
        out List<CommandParser.ArgToken> argTokens,
        out string argsRaw,
        out CommandResult result)
    {
        bundle = null!;
        argTokens = null!;
        argsRaw = "";
        result = CommandResult.Unmatched;

        string first = tokens[0].Value;

        if (string.Equals(first, CommandRegistry.FallbackRoot, StringComparison.OrdinalIgnoreCase))
            return ResolveQualified(tokens, ctx, rest, out bundle, out argTokens, out argsRaw, out result);

        // Literal subcommand beats a generic argument route on the same first token.
        if (tokens.Count >= 2 && CommandRegistry.TryGetGroupedSlot(first, tokens[1].Value, out var grouped))
        {
            if (grouped.Ambiguous)
            {
                ReplyAmbiguous(ctx, first + " " + tokens[1].Value, grouped);
                result = CommandResult.Executed;
                return false;
            }
            if (grouped.Bundle != null)
            {
                bundle = grouped.Bundle;
                SliceArgs(tokens, rest, pathTokens: 2, out argTokens, out argsRaw);
                result = CommandResult.Executed;
                return true;
            }
        }

        if (CommandRegistry.TryGetRootSlot(first, out var root))
        {
            if (root.Reserved)
            {
                result = CommandResult.Unmatched;
                return false;
            }
            if (root.Ambiguous)
            {
                ReplyAmbiguous(ctx, first, root);
                result = CommandResult.Executed;
                return false;
            }
            if (root.Bundle != null)
            {
                bundle = root.Bundle;
                SliceArgs(tokens, rest, pathTokens: 1, out argTokens, out argsRaw);
                result = CommandResult.Executed;
                return true;
            }
        }

        if (CommandRegistry.TryGetGroup(first, out _))
        {
            ReplyGroupUsage(ctx, first);
            result = CommandResult.Executed;
            return false;
        }

        result = CommandResult.Unmatched;
        return false;
    }

    private static bool ResolveQualified(
        List<CommandParser.ArgToken> tokens,
        ICommandContext ctx,
        string rest,
        out CommandBundle bundle,
        out List<CommandParser.ArgToken> argTokens,
        out string argsRaw,
        out CommandResult result)
    {
        bundle = null!;
        argTokens = null!;
        argsRaw = "";

        if (tokens.Count < 2)
        {
            ctx.Reply($"Usage: {CommandRegistry.Prefix}{CommandRegistry.FallbackRoot} <owner> <command> [args].");
            result = CommandResult.Executed;
            return false;
        }

        string ownerId = tokens[1].Value;
        if (!CommandRegistry.TryGetQualifiedOwner(ownerId, out var byPath))
        {
            ctx.Reply($"Unknown command owner '{Progenitor.Chat.ChatLinks.Defuse(ownerId)}'. Try {CommandRegistry.Prefix}help.");
            result = CommandResult.Executed;
            return false;
        }

        if (tokens.Count >= 4)
        {
            string groupedPath = tokens[2].Value + " " + tokens[3].Value;
            if (byPath.TryGetValue(groupedPath, out var groupedBundle) && groupedBundle != null)
            {
                bundle = groupedBundle;
                SliceArgs(tokens, rest, pathTokens: 4, out argTokens, out argsRaw);
                result = CommandResult.Executed;
                return true;
            }
        }

        if (tokens.Count >= 3 && byPath.TryGetValue(tokens[2].Value, out var named) && named != null)
        {
            bundle = named;
            SliceArgs(tokens, rest, pathTokens: 3, out argTokens, out argsRaw);
            result = CommandResult.Executed;
            return true;
        }

        if (tokens.Count >= 3)
        {
            var group = CommandRegistry.ListGroup(tokens[2].Value);
            var owned = new List<CommandBundle>();
            foreach (var b in group)
            {
                if (string.Equals(b.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                    owned.Add(b);
            }
            if (owned.Count > 0)
            {
                ctx.Reply($"{CommandRegistry.Prefix}{CommandRegistry.FallbackRoot} {owned[0].OwnerId} {owned[0].GroupName} commands:");
                foreach (var b in owned)
                    ctx.Reply("  " + CommandRegistry.FormatHelpLine(b));
                result = CommandResult.Executed;
                return false;
            }
        }

        ctx.Reply($"Unknown command for owner '{Progenitor.Chat.ChatLinks.Defuse(ownerId)}'. Try {CommandRegistry.Prefix}help.");
        result = CommandResult.Executed;
        return false;
    }

    private static void ReplyAmbiguous(ICommandContext ctx, string route, RouteSlot slot) =>
        ctx.Reply(CommandRegistry.FormatConflictReply(route, slot));

    private static void SliceArgs(
        List<CommandParser.ArgToken> tokens,
        string rest,
        int pathTokens,
        out List<CommandParser.ArgToken> argTokens,
        out string argsRaw)
    {
        argTokens = new List<CommandParser.ArgToken>();
        for (int i = pathTokens; i < tokens.Count; i++) argTokens.Add(tokens[i]);
        argsRaw = argTokens.Count == 0 ? "" : rest.Substring(argTokens[0].Start);
    }

    private static CommandResult Invoke(
        ICommandContext ctx,
        CommandBundle bundle,
        List<CommandParser.ArgToken> argTokens,
        string argsRaw)
    {
        var usable = new List<CommandOverload>();
        bool listedAdmin = CommandAccess.ListedAdminFor(ctx, bundle);
        foreach (var o in bundle.Overloads)
        {
            if (!o.UsableBy(ctx.IsAdmin, listedAdmin)) continue;
            usable.Add(o);
        }

        if (usable.Count == 0)
        {
            ctx.Reply("This command is admin-only.");
            return CommandResult.Denied;
        }

        var bound = new List<BoundOverload>();
        foreach (var o in usable)
        {
            if (TryBind(o, ctx, argTokens, argsRaw, out var boundArgs, out int score, out int defaultsUsed))
                bound.Add(new BoundOverload(o, boundArgs, score, defaultsUsed));
        }

        if (bound.Count == 0)
        {
            ReplyUsages(ctx, bundle, usable);
            return CommandResult.Executed;
        }

        bound.Sort(CompareBound);
        if (bound.Count > 1 && CompareBound(bound[0], bound[1]) == 0)
        {
            ctx.Reply("Ambiguous command; possible usages:");
            var seen = new HashSet<CommandOverload>();
            foreach (var b in bound)
            {
                if (!seen.Add(b.Overload)) continue;
                if (CompareBound(b, bound[0]) != 0) break;
                ctx.Reply("  " + CommandRegistry.FormatUsage(bundle, b.Overload));
            }
            return CommandResult.Executed;
        }

        var winner = bound[0];
        // WINDOWS1: the handler's own replies carry its plugin's name (ModReplyContext); the framework's lines above do not.
        if (winner.Args.Length > 0 && winner.Args[0] is ICommandContext plain)
            winner.Args[0] = ModReplyContext.For(plain, bundle.Owner);
        try
        {
            winner.Overload.Method.Invoke(winner.Overload.Instance, winner.Args);
            return CommandResult.Executed;
        }
        catch (TargetInvocationException e)
        {
            Plugin.Logger?.LogError($"Commands: '{CommandRegistry.Prefix}{bundle.DisplayPath}' threw: {e.InnerException ?? e}");
            ctx.Reply("Command failed.");
            return CommandResult.Error;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: '{CommandRegistry.Prefix}{bundle.DisplayPath}' threw: {e}");
            ctx.Reply("Command failed.");
            return CommandResult.Error;
        }
    }

    private static int CompareBound(BoundOverload a, BoundOverload b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;
        return a.DefaultsUsed.CompareTo(b.DefaultsUsed);
    }

    private static bool TryBind(
        CommandOverload overload,
        ICommandContext ctx,
        List<CommandParser.ArgToken> tokens,
        string argsRaw,
        out object?[] invokeArgs,
        out int score,
        out int defaultsUsed)
    {
        var parameters = overload.Parameters;
        invokeArgs = new object?[parameters.Length + 1];
        invokeArgs[0] = ctx;
        score = 0;
        defaultsUsed = 0;
        int tokenIndex = 0;

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            bool remainder = overload.HasRemainder && i == parameters.Length - 1;
            if (remainder)
            {
                string rest = RemainderFrom(argsRaw, tokens, tokenIndex);
                if (rest.Length == 0 && p.HasDefaultValue)
                {
                    invokeArgs[i + 1] = p.DefaultValue ?? "";
                    defaultsUsed++;
                }
                else
                {
                    invokeArgs[i + 1] = rest;
                    if (rest.Length > 0) score += 1;
                }
                tokenIndex = tokens.Count;
                continue;
            }

            if (tokenIndex >= tokens.Count)
            {
                if (!p.HasDefaultValue) return false;
                invokeArgs[i + 1] = p.DefaultValue;
                defaultsUsed++;
                continue;
            }

            if (!CommandParser.TryConvert(tokens[tokenIndex].Value, p.ParameterType, out var converted, out int spec))
                return false;
            invokeArgs[i + 1] = converted;
            score += spec;
            tokenIndex++;
        }

        if (tokenIndex < tokens.Count) return false;
        return true;
    }

    private static string RemainderFrom(string argsRaw, List<CommandParser.ArgToken> tokens, int consumed)
    {
        // argsRaw starts at tokens[0] in the original rest string, so token.Start
        // values have to be shifted down by tokens[0].Start to index into argsRaw.
        if (tokens.Count == 0 || consumed >= tokens.Count) return "";
        int offset = tokens[consumed].Start - tokens[0].Start;
        if (offset <= 0) return argsRaw;
        if (offset >= argsRaw.Length) return "";
        return argsRaw.Substring(offset);
    }

    private static void ReplyUsages(ICommandContext ctx, CommandBundle bundle, List<CommandOverload> overloads)
    {
        if (overloads.Count == 1)
        {
            var o = overloads[0];
            ctx.Reply(CommandRegistry.FormatUsage(bundle, o));
            if (!string.IsNullOrEmpty(o.Attribute.Description))
                ctx.Reply(o.Attribute.Description!);
            return;
        }

        ctx.Reply($"Usage of {CommandRegistry.Prefix}{bundle.DisplayPath}:");
        foreach (var o in overloads)
            ctx.Reply("  " + CommandRegistry.FormatUsage(bundle, o));
    }

    private static void ReplyGroupUsage(ICommandContext ctx, string groupToken)
    {
        var commands = CommandRegistry.ListGroup(groupToken);
        var visible = new List<CommandBundle>();
        foreach (var b in commands)
            if (b.VisibleTo(ctx.IsAdmin, CommandAccess.ListedAdminFor(ctx, b))) visible.Add(b);

        if (visible.Count == 0)
        {
            ctx.Reply($"Unknown subcommand. Try {CommandRegistry.Prefix}help.");
            return;
        }

        ctx.Reply($"{CommandRegistry.Prefix}{visible[0].GroupName} commands:");
        foreach (var b in visible)
            ctx.Reply("  " + FormatHelpLine(b));
    }

    internal static string FormatHelpLine(CommandBundle bundle) =>
        CommandRegistry.FormatHelpLine(bundle);

    private readonly struct BoundOverload
    {
        public readonly CommandOverload Overload;
        public readonly object?[] Args;
        public readonly int Score;
        public readonly int DefaultsUsed;

        public BoundOverload(CommandOverload overload, object?[] args, int score, int defaultsUsed)
        {
            Overload = overload;
            Args = args;
            Score = score;
            DefaultsUsed = defaultsUsed;
        }
    }
}
