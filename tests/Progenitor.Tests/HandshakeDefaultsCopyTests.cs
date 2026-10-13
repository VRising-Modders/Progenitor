using System;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job HS1 (2026-09-11). Pins the player-facing copy a Progenitor client actually renders on a
/// refused or deferred join under the shipped defaults: deny panel for a kick, consent panel for
/// an install window. Vanilla has none of this UI; its native string is asserted in
/// <see cref="HandshakeDefaultsTests"/> (timeout / mismatch reasons) and measured live.
///
/// <para>Each test names the template it pins. Changing the headline, the kick-reason quote, the
/// "nothing was downloaded" paragraph, or the required/received version line turns the matching
/// test red.</para>
/// </summary>
public sealed class HandshakeDefaultsCopyTests
{
    /// <summary>
    /// Breaks if one missing required mod stops naming the id, the required version, "you have
    /// nothing", or the join-check sentence the disconnect box uses.
    /// </summary>
    [Fact]
    public void Deny_copy_for_one_missing_required_mod()
    {
        var asked = new[]
        {
            Declared("need.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var rows = ModManifestDeny.BuildLocalRows(asked, Array.Empty<Handshake.ModRecord>());
        var report = new ModDenyReport(
            "Mod mismatch: need.mod: expected 1.2.0, got missing",
            "127.0.0.1:9976",
            "Night Garden",
            "",
            "",
            descriptorReceived: false,
            rows);

        string text = ModDenyText.Compose(report);
        Assert.Equal("'Night Garden' refused this connection: 1 mod does not match.", ModDenyText.Headline(report));
        Assert.Contains("The server's reason: Mod mismatch: need.mod: expected 1.2.0, got missing", text, StringComparison.Ordinal);
        Assert.Contains(ModDenyText.NothingWasDownloaded, text, StringComparison.Ordinal);
        Assert.Contains(ModDenyText.RestartNote, text, StringComparison.Ordinal);
        Assert.Contains("need.mod  server requires 1.2.0  you have nothing", text, StringComparison.Ordinal);
        Assert.Contains("  join check: need.mod: the server needs it (1.2.0), but your game doesn't have it. Install it from the join screens", text, StringComparison.Ordinal);
        Assert.Contains("the server named no source for this mod", text, StringComparison.Ordinal);
        Assert.Single(rows);
        Assert.Equal("1.2.0", rows[0].RequiredVersion);
        Assert.Null(rows[0].Have);
        Assert.True(rows[0].Missing);
    }

    /// <summary>
    /// Breaks if several missing mods stop listing every row on the deny panel. The kick string
    /// may truncate; this panel is the remainder path on a Progenitor client.
    /// </summary>
    [Fact]
    public void Deny_copy_for_several_missing_required_mods_lists_every_row()
    {
        var asked = new[]
        {
            Declared("a.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("b.mod", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("c.mod", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var rows = ModManifestDeny.BuildLocalRows(asked, Array.Empty<Handshake.ModRecord>());
        var problems = Handshake.Compare.Evaluate(asked, Array.Empty<Handshake.ModRecord>());
        string kick = Handshake.Compare.MismatchReason(problems, note: null);
        var report = new ModDenyReport(kick, "127.0.0.1:9976", "Test", "", "", false, rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal("'Test' refused this connection: 3 mods do not match.", ModDenyText.Headline(report));
        string text = ModDenyText.Compose(report);
        Assert.Contains("a.mod  server requires 1.0  you have nothing", text, StringComparison.Ordinal);
        Assert.Contains("b.mod  server requires 2.0  you have nothing", text, StringComparison.Ordinal);
        Assert.Contains("c.mod  server requires 3.0  you have nothing", text, StringComparison.Ordinal);
        Assert.Contains("a.mod: the server needs it (1.0), but your game doesn't have it. Install it from the join screens", text, StringComparison.Ordinal);
        Assert.Contains("c.mod: the server needs it (3.0), but your game doesn't have it. Install it from the join screens", text, StringComparison.Ordinal);
        Assert.Contains("3 mods do not match", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks if the wrong-version row stops showing both the required version (from the server's
    /// request) and the received version (from this client's Provide), or if the join-check line
    /// drifts from <c>Compare.Check</c>.
    /// </summary>
    [Fact]
    public void Deny_copy_for_wrong_version_shows_required_and_received()
    {
        var asked = new[]
        {
            Declared("need.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var have = new[]
        {
            Declared("need.mod", "1.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var rows = ModManifestDeny.BuildLocalRows(asked, have);
        var row = Assert.Single(rows);
        Assert.Equal("1.2.0", row.RequiredVersion);
        Assert.Equal("1.1.0", row.Have);
        Assert.False(row.Missing);
        Assert.Equal("need.mod: expected 1.2.0, got 1.1.0", row.EnforcedKickReason);

        var report = new ModDenyReport(
            "Mod mismatch: need.mod: expected 1.2.0, got 1.1.0",
            "127.0.0.1:9976",
            "",
            "",
            "",
            false,
            rows);
        string text = ModDenyText.Compose(report);
        Assert.Contains("need.mod  server requires 1.2.0  you have 1.1.0", text, StringComparison.Ordinal);
        Assert.Contains("  join check: need.mod: expected 1.2.0, got 1.1.0", text, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:9976 refused this connection: 1 mod does not match.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks if a timeout Result is quoted as if the server had seen this client's mods. The
    /// deny panel may still list local rows from ServerAsked + Provided, but the kick line itself
    /// must stay the no-answer sentence.
    /// </summary>
    [Fact]
    public void Timeout_kick_line_on_the_deny_panel_does_not_claim_a_received_inventory()
    {
        string kick = Handshake.Compare.NoAnswerReason(
            Handshake.DefaultAnswerTimeoutSeconds,
            "need.mod 1.0 ServerMustHave/Exact");
        var report = new ModDenyReport(kick, "127.0.0.1:9976", "Night Garden", "", "", false, Array.Empty<ModDenyRow>());
        Assert.Equal(
            "The server's reason: Progenitor: no handshake answer within 20 s (server requires: need.mod 1.0 ServerMustHave/Exact)",
            ModDenyText.KickReasonLine(report));
        Assert.DoesNotContain("got missing", ModDenyText.KickReasonLine(report), StringComparison.Ordinal);
        Assert.Equal(
            "'Night Garden' refused this connection over a mod mismatch.",
            ModDenyText.Headline(report));
    }

    /// <summary>
    /// Breaks if the deferred-join headline stops being the server's own
    /// <c>DeferredResultMessage</c>, or if a countdown number is invented beside it.
    /// </summary>
    [Fact]
    public void Deferred_headline_is_the_servers_sentence_with_no_countdown()
    {
        string server = Handshake.Compare.DeferredResultMessage(
            new[] { "need.mod" }, Handshake.DefaultInstallGraceSeconds);
        Assert.Equal(server, ModInstallWindowPolicy.Headline(server));
        Assert.Contains("60 s", ModInstallWindowPolicy.Headline(server), StringComparison.Ordinal);
        Assert.Equal(ModInstallWindowPolicy.WaitingNote, ModInstallWindowPolicy.Headline(""));
        Assert.Contains("there is no countdown here", ModInstallWindowPolicy.WaitingNote, StringComparison.Ordinal);
    }

    private static Handshake.ModRecord Declared(
        string guid,
        string version,
        NetworkCompatibilityLevel level,
        NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(guid, version, level, rule);
}
