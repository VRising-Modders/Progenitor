using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C5: the Sample's <c>.compat</c> lines (<c>samples/Progenitor.Sample/SampleCompat.cs</c>). The
/// command and the client's report are game-bound and pinned by source; the words are game-free.
/// </summary>
public class SampleCompatTests
{
    private sealed class FakePeer : IPeerView
    {
        public bool Known { get; init; }
        public string? CoreVersion { get; init; }
        public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Features { get; } = new(StringComparer.Ordinal);

        public bool Has(string feature, int minLevel = 1) => Known && Features.Contains(feature);

        public bool Has(string modGuid, string feature, int minLevel = 1) => Known && Features.Contains(feature);

        public string? ModVersion(string modGuid) => Versions.TryGetValue(modGuid, out var v) ? v : null;
    }

    [Fact]
    public void Offered_features_are_listed_in_order_or_said_to_be_none()
    {
        Assert.Equal("this client offers camera=1, zoom=2.",
            SampleCompat.OfferedLine("this client", new Dictionary<string, int> { ["zoom"] = 2, ["camera"] = 1 }));
        Assert.Equal("This server's Sample offers no features.", SampleCompat.OfferedLine("This server's Sample", new Dictionary<string, int>()));
        Assert.Equal("x offers no features.", SampleCompat.OfferedLine("x", null));
    }

    [Fact]
    public void The_peer_line_names_its_versions_and_the_camera_feature_or_says_nothing_is_known()
    {
        var peer = new FakePeer { Known = true, CoreVersion = "2026.10.162" };
        peer.Versions["progenitor.sample"] = "2026.10.150";
        peer.Features.Add(SampleCompat.FeatureCamera);
        Assert.Equal("Your client: Progenitor 2026.10.162; this mod 2026.10.150; camera yes.",
            SampleCompat.PeerLine("Your client", peer, "progenitor.sample"));

        var without = new FakePeer { Known = true, CoreVersion = null };
        Assert.Equal("the joined server: Progenitor unknown; this mod not running; camera no.",
            SampleCompat.PeerLine("the joined server", without, "progenitor.sample"));

        Assert.Contains("no feature table yet", SampleCompat.PeerLine("Your client", new FakePeer(), "progenitor.sample"), StringComparison.Ordinal);
        Assert.Contains("no feature table yet", SampleCompat.PeerLine("Your client", null, "progenitor.sample"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_reply_says_where_to_look_or_why_nothing_went_out()
    {
        Assert.Contains("client log", SampleCompat.ReportReply(SendResult.Sent), StringComparison.Ordinal);
        Assert.Equal("Could not ask your client (PeerNotReady).", SampleCompat.ReportReply(SendResult.PeerNotReady));
    }

    /// <summary>The report request is a valid message type, and the plugin wires the feature and the command.</summary>
    [Fact]
    public void The_report_message_is_valid_and_the_plugin_wires_it()
    {
        var schema = MessageSchema.For(typeof(CompatReportRequest));
        Assert.Equal("compat.report", schema.Message!.Name);

        string compat = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleCompat.cs");
        SourcePin.AssertUncommented(compat, "ctx.Features.Offer(FeatureCamera);");
        SourcePin.AssertUncommented(compat, "ctx.Messages.On<CompatReportRequest>(");
        SourcePin.AssertUncommented(compat, "plugin.Peers.Of(ctx.UserEntity)");
        SourcePin.AssertUncommented(compat, "PeerLine(\"the joined server\", ctx.Peer, ctx.Guid)");
        string plugin = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "SampleCompat.Attach(ctx);");
    }
}
