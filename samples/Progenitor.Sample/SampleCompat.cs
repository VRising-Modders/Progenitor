using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progenitor.Networking;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.compat</c>: feature negotiation (<c>ctx.Features</c>, <c>ctx.Peer</c>, <c>ctx.Peers</c>;
/// docs/modules.md, "Feature negotiation and versioned messages"). The client offers the feature
/// <c>camera</c> (it applies the <c>.shot</c> packet, <see cref="SampleCamera"/>). Chat commands run on
/// the server, so <c>.compat</c> replies with what the server offers and the caller's client as the
/// server sees it (<c>ctx.Peers.Of(user)</c>), then asks the client with a versioned message
/// (<see cref="CompatReportRequest"/>) to log what it offers and the joined server as it sees it
/// (<c>ctx.Peer</c>): <c>Sample compat: …</c> lines in the client log and the Ctrl+F9 panel.
///
/// <para>The Sample's own packets stay raw <c>ctx.Net</c>: it is declared <c>Exact</c>, so both sides
/// always run the same build. The formatting here is game-free and tested
/// (<c>tests/Progenitor.Tests/SampleCompatTests.cs</c>). Source-checked, built and game-free tested;
/// not verified live.</para>
/// </summary>
internal static class SampleCompat
{
    internal const string Area = "compat";

    /// <summary>Offered by the client: it applies the <c>.shot</c> camera packet.</summary>
    internal const string FeatureCamera = "camera";

    internal const string UsageReply = "Usage: .compat";

    /// <summary>
    /// One line for what this side offers: <c>the server offers no features.</c> or
    /// <c>this client offers camera=1.</c>
    /// </summary>
    internal static string OfferedLine(string who, IReadOnlyDictionary<string, int>? offered)
    {
        if (offered == null || offered.Count == 0) return who + " offers no features.";
        var parts = offered
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
        return who + " offers " + string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// The other side as <paramref name="peer"/> sees it, one line: whether its feature table arrived,
    /// its Progenitor version, its version of this mod and whether it offers <see cref="FeatureCamera"/>.
    /// </summary>
    internal static string PeerLine(string who, IPeerView? peer, string modGuid)
    {
        if (peer == null || !peer.Known)
            return who + ": no feature table yet (not joined, still joining, or its Progenitor predates feature negotiation).";
        return who + ": Progenitor " + (peer.CoreVersion ?? "unknown")
            + "; this mod " + (peer.ModVersion(modGuid) ?? "not running")
            + "; " + FeatureCamera + " " + (peer.Has(FeatureCamera) ? "yes" : "no") + ".";
    }

    /// <summary>The server's reply about the request it sent to the caller's client.</summary>
    internal static string ReportReply(SendResult result) => result == SendResult.Sent
        ? "Asked your client for its view; see your client log (Sample compat: ...)."
        : "Could not ask your client (" + result + ").";

#if !PROGENITOR_TESTS
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (ctx.IsServer)
        {
            ctx.Commands.Register(typeof(SampleCompatCommands));
            return;
        }

        // Tracked: unload withdraws the offer and the handler.
        ctx.Features.Offer(FeatureCamera);
        ctx.Messages.On<CompatReportRequest>((from, request, info) => LogClientView(ctx));
    }

    private static void LogClientView(PluginContext ctx)
    {
        try
        {
            SampleLog.Info(Area, OfferedLine("this client", ctx.Features.Offered));
            SampleLog.Info(Area, PeerLine("the joined server", ctx.Peer, ctx.Guid));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample compat report threw: " + e);
        }
    }
#endif
}

/// <summary>Server to client: "log what you offer and how you see this server". Version 1.</summary>
[ModMessage("compat.report")]
internal sealed class CompatReportRequest
{
    /// <summary>Not read by version 1; a later version may number its own fields after it.</summary>
    [Field(1)] public int Sequence;
}

#if !PROGENITOR_TESTS
internal static class SampleCompatCommands
{
    // Not adminOnly: it reports this plugin's own offers and the caller's own client.
    [Command("compat", usage: SampleCompat.UsageReply,
        description: "Feature negotiation: what this mod offers on the server, your client as the server sees it, and (in your client log) the server as your client sees it.")]
    public static void Run(ICommandContext ctx)
    {
        try
        {
            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            ctx.Reply(SampleCompat.OfferedLine("This server's Sample", plugin.Features.Offered));
            ctx.Reply(SampleCompat.PeerLine("Your client, as this server sees it", plugin.Peers.Of(ctx.UserEntity), plugin.Guid));
            var result = plugin.Messages.SendTo(ctx.UserEntity, new CompatReportRequest());
            ctx.Reply(SampleCompat.ReportReply(result));
            SampleLog.Info(SampleCompat.Area, "server asked the client for its view: " + result);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .compat threw: " + e);
            ctx.Reply("Sample compat failed. See the server log.");
        }
    }
}
#endif
