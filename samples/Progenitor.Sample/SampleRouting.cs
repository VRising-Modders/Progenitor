using Progenitor.Commands;
using Progenitor.Plugins;

namespace Progenitor.Sample;

/// <summary>
/// Two command owners registered from this plugin so the routing contract is visible in chat:
/// a unique shortcut, the qualified fallback to the same handler, a shared group with distinct
/// literal subcommands, and one intentional shortcut collision. Owner ids are explicit (not the
/// Sample display title). Handles are tracked; unload drops both owners.
/// </summary>
internal static class SampleRouting
{
    /// <summary>Stable owner id for the alpha command set. Not the plugin display name.</summary>
    internal const string AlphaId = "progenitor.sample.alpha";

    /// <summary>Stable owner id for the beta command set.</summary>
    internal const string BetaId = "progenitor.sample.beta";

    internal const string SparkName = "spark";
    internal const string FlareName = "flare";
    internal const string RouteGroup = "route";
    internal const string IronName = "iron";
    internal const string SteelName = "steel";

    internal const string SparkReply = "spark from alpha";
    internal const string FlareAlphaReply = "flare from alpha";
    internal const string FlareBetaReply = "flare from beta";
    internal const string IronReply = "route iron from alpha";
    internal const string SteelReply = "route steel from beta";

    internal static void Attach(PluginContext ctx)
    {
        // Two owner ids on the same plugin lifetime: routing treats them as distinct mods;
        // ctx.Track + ctx.Owner still drop both on unload/swap.
        ctx.Track(CommandRegistry.RegisterType(typeof(AlphaCommands), ctx.Owner, AlphaId));
        ctx.Track(CommandRegistry.RegisterType(typeof(AlphaRouteCommands), ctx.Owner, AlphaId));
        ctx.Track(CommandRegistry.RegisterType(typeof(BetaCommands), ctx.Owner, BetaId));
        ctx.Track(CommandRegistry.RegisterType(typeof(BetaRouteCommands), ctx.Owner, BetaId));
        ctx.Log.LogInfo(
            "Sample routing: ." + SparkName + " (alpha shortcut), ." + RouteGroup + " " + IronName
            + " / ." + RouteGroup + " " + SteelName + " (shared group), ." + FlareName
            + " (intentional conflict; use .p " + AlphaId + " " + FlareName + " or .p " + BetaId
            + " " + FlareName + ").");
    }
}

/// <summary>
/// Alpha owner, root commands. <c>.spark</c> is uncontested. <c>.flare</c> collides with beta.
/// Qualified: <c>.p progenitor.sample.alpha spark</c> (same handler as <c>.spark</c>).
/// </summary>
internal static class AlphaCommands
{
    [Command(SampleRouting.SparkName)]
    public static void Spark(ICommandContext ctx) => ctx.Reply(SampleRouting.SparkReply);

    [Command(SampleRouting.FlareName)]
    public static void Flare(ICommandContext ctx) => ctx.Reply(SampleRouting.FlareAlphaReply);
}

/// <summary>
/// Alpha owner, grouped. Shares <c>.route</c> with beta; this is the <c>iron</c> literal.
/// </summary>
[CommandGroup(SampleRouting.RouteGroup)]
internal static class AlphaRouteCommands
{
    [Command(SampleRouting.IronName)]
    public static void Iron(ICommandContext ctx) => ctx.Reply(SampleRouting.IronReply);
}

/// <summary>
/// Beta owner, root commands. <c>.flare</c> collides with alpha; qualified route still runs.
/// </summary>
internal static class BetaCommands
{
    [Command(SampleRouting.FlareName)]
    public static void Flare(ICommandContext ctx) => ctx.Reply(SampleRouting.FlareBetaReply);
}

/// <summary>
/// Beta owner, grouped. Shares <c>.route</c> with alpha; this is the <c>steel</c> literal.
/// </summary>
[CommandGroup(SampleRouting.RouteGroup)]
internal static class BetaRouteCommands
{
    [Command(SampleRouting.SteelName)]
    public static void Steel(ICommandContext ctx) => ctx.Reply(SampleRouting.SteelReply);
}
