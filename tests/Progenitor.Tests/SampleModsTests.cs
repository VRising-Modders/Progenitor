using System;
using System.IO;
using Progenitor.Commands;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// R25 findings 1 and 13: <c>.mods trust</c> is client-origin; a server-sent SmMd cannot pin the operator. SampleMods
/// registers its receive handler through PluginContext in the live source. (The Sample's self-offer and the consent
/// demo went with server-to-client mod distribution, 2026-10-05.)
/// </summary>
[Collection("ModManifest")]
public sealed class SampleModsTests : IDisposable
{
    private readonly string _dir;
    private readonly ModManifestKey _key;
    private readonly FakeModsCtx _ctx = new();

    public SampleModsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-samplemods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        SampleMods.TestForceClient = false;
        SampleMods.TestLastReply = null;
        _key = ModManifestCrypto.Generate();
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        _key.Dispose();
        SampleMods.TestForceClient = false;
        SampleMods.TestLastReply = null;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Server_mods_trust_replies_run_on_your_client()
    {
        SampleMods.TestForceClient = false;
        SampleMods.Handle(_ctx, "trust", "");
        Assert.Equal(SampleMods.RunOnClientReply, SampleMods.TestLastReply);
        Assert.False(File.Exists(Path.Combine(_dir, ModManifest.TrustedFileName)));
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Infos, line => line.Contains("server-origin", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first verified catalog already pins the operator (trust on first use, 2026-10-05); a local
    /// <c>.mods trust</c> (session) still succeeds and writes nothing more.
    /// </summary>
    [Fact]
    public void Local_mods_trust_pins_the_pending_operator()
    {
        DeliverCatalog();
        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        string before = File.ReadAllText(path);
        SampleMods.TestForceClient = true;
        SampleMods.Handle(_ctx, "trust", "");
        Assert.True(ModManifest.Pending!.OperatorTrusted);
        Assert.Equal(before, File.ReadAllText(path));
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Infos, line => line.StartsWith("Sample: .mods trust fp=", StringComparison.Ordinal) && line.Contains("ok=True", StringComparison.Ordinal));
    }

    [Fact]
    public void Server_sent_SmMd_cannot_change_the_trust_file()
    {
        DeliverCatalog();
        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        string before = File.ReadAllText(path);
        SampleMods.ApplyPacketForTests(3);
        Assert.Equal(before, File.ReadAllText(path));
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Warnings, line => line.Contains("trust is client-origin only", StringComparison.Ordinal));
    }

    [Fact]
    public void Live_source_registers_through_PluginContext()
    {
        string src = ReadRepoFile("samples", "Progenitor.Sample", "SampleMods.cs");
        SourcePin.AssertStatement(src, "ctx.Handshake.Track(ctx.Net.OnReceive(EventId, OnPacket))");
        Assert.DoesNotContain("OpTrustAlways", src, StringComparison.Ordinal);
        Assert.DoesNotContain("OfferPackage", src, StringComparison.Ordinal);
        Assert.DoesNotContain("ModManifest.Offer(", src, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomEvents.OnReceive(EventId", src, StringComparison.Ordinal);
    }

    private void DeliverCatalog()
    {
        var body = ModManifestJson.BuildBody(
            new[]
            {
                new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            },
            new ManifestOverrides { ServerName = "Night Garden", OperatorName = "Ada" },
            _key.ToOperator("Ada"),
            new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        var envelope = new ManifestEnvelope(body, _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body))));
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(string.Join("/", parts) + " not found");
    }

    private sealed class FakeModsCtx : ICommandContext
    {
        public void Reply(string text) { }
    }

    // ---- Job J21: ctx.Name / ctx.Version ----

    /// <summary>
    /// The three metadata members in one line. <c>ctx.Guid</c> already had examples;
    /// <c>ctx.Name</c> and <c>ctx.Version</c> had none, and this is the command where they mean
    /// something, because they are what the handshake row carries.
    /// </summary>
    [Fact]
    public void About_reports_the_name_the_version_and_the_guid_with_the_side()
    {
        string reply = SampleMods.AboutReply("Progenitor Sample", "0.1.0", "progenitor.sample", isServer: true);

        Assert.Contains("ctx.Name=\"Progenitor Sample\"", reply, StringComparison.Ordinal);
        Assert.Contains("ctx.Version=\"0.1.0\"", reply, StringComparison.Ordinal);
        Assert.Contains("ctx.Guid=\"progenitor.sample\"", reply, StringComparison.Ordinal);
        Assert.Contains("side=server", reply, StringComparison.Ordinal);
        Assert.Contains("side=client", SampleMods.AboutReply("n", "v", "g", isServer: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule a reader must take away: the GUID is the identity, the other two are for people.
    /// Keying anything off <c>ctx.Name</c> is the mistake this line exists to prevent.
    /// </summary>
    [Fact]
    public void About_says_the_guid_is_the_identity_and_the_name_is_not()
    {
        string reply = SampleMods.AboutReply("n", "v", "g", true);

        Assert.Contains("the GUID is the identity", reply, StringComparison.Ordinal);
        Assert.Contains("never key logic off the name", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_metadata_field_is_reported_rather_than_printed_as_an_empty_string()
    {
        string reply = SampleMods.AboutReply(null, "", "g", true);

        Assert.Contains("ctx.Name=\"(unset)\"", reply, StringComparison.Ordinal);
        Assert.Contains("ctx.Version=\"(unset)\"", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Mods_about_replies_with_the_metadata_line_on_either_side()
    {
        SampleMods.TestForceClient = false;
        SampleMods.Handle(_ctx, "about", "");
        Assert.Contains("ctx.Name=", SampleMods.TestLastReply ?? "", StringComparison.Ordinal);

        SampleMods.TestForceClient = true;
        SampleMods.TestLastReply = null;
        SampleMods.Handle(_ctx, "about", "");
        Assert.Contains("ctx.Guid=", SampleMods.TestLastReply ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_action_replies_the_usage_line_which_now_lists_about()
    {
        SampleMods.Handle(_ctx, "wat", "");

        Assert.Equal(SampleMods.UsageReply, SampleMods.TestLastReply);
        Assert.Contains(".mods about", SampleMods.UsageReply, StringComparison.Ordinal);
    }
}
