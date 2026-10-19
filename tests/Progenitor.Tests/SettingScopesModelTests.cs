using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Settings scopes step 2 (docs/settings-scopes.md, decisions 2026-10-03): scope names, owning
/// half, the availability decision, "when" rules and the two-section grouping. Game-free model only;
/// nothing here is drawn or sent yet.
/// </summary>
public class SettingScopesModelTests
{
    // ---- scope names ------------------------------------------------------------------------------

    [Fact]
    public void Personal_and_ServerWide_are_the_old_values_under_new_names()
    {
        Assert.Equal(SettingScope.Local, SettingScope.Personal);
        Assert.Equal(SettingScope.Server, SettingScope.ServerWide);
        Assert.Equal(0, (int)SettingScope.Personal);
        Assert.Equal(1, (int)SettingScope.ServerWide);
    }

    [Fact]
    public void Readback_names_stay_as_before_and_the_new_words_are_explicit()
    {
        Assert.Equal("Local", SettingScopes.LegacyName(SettingScope.Personal));
        Assert.Equal("Server", SettingScopes.LegacyName(SettingScope.ServerWide));
        Assert.Equal("personal", SettingScopes.Word(SettingScope.Local));
        Assert.Equal("server-wide", SettingScopes.Word(SettingScope.Server));
        Assert.Equal("client", SettingScopes.Word(SettingOwner.Client));
        Assert.Equal("server", SettingScopes.Word(SettingOwner.Server));
    }

    [Fact]
    public void A_descriptor_readback_still_says_Local_or_Server()
    {
        var personal = new SettingDescriptor("m", "S", "K", "System.Boolean", "true", "true", "", "", 0,
            SettingsEditorHint.Toggle, SettingCommitPolicy.Immediate, SettingScope.Personal, SettingEditPath.LocalConfig);
        var server = new SettingDescriptor("m", "S", "K", "System.Int32", "1", "1", "", "", 0,
            SettingsEditorHint.Numeric, SettingCommitPolicy.OnSubmit, SettingScope.ServerWide, SettingEditPath.None);
        Assert.Contains("\"scope\":\"Local\"", personal.ToJson());
        Assert.Contains("\"scope\":\"Server\"", server.ToJson());
    }

    [Theory]
    [InlineData(SettingScope.Personal, false, 0)]
    [InlineData(SettingScope.Personal, true, 1)]
    [InlineData(SettingScope.ServerWide, false, 1)]
    [InlineData(SettingScope.ServerWide, true, 1)]
    public void Server_wide_is_always_server_owned(SettingScope scope, bool declaredByServer, int expected) =>
        Assert.Equal((SettingOwner)expected, SettingScopes.OwnerOf(scope, declaredByServer));

    // ---- availability -----------------------------------------------------------------------------

    private static SettingAvailabilityResult Decide(SettingScope scope, SettingOwner owner, Func<SettingAvailabilityFacts, SettingAvailabilityFacts>? edit = null)
    {
        var facts = SettingAvailabilityFacts.Ready(scope, owner);
        if (edit != null) facts = edit(facts);
        return SettingAvailability.Decide(facts);
    }

    [Fact]
    public void A_client_owned_personal_setting_is_editable_offline_and_anywhere()
    {
        foreach (SettingConnection connection in Enum.GetValues(typeof(SettingConnection)))
        {
            var result = Decide(SettingScope.Personal, SettingOwner.Client, f => f with
            {
                Connection = connection, ManifestArrived = false, ServerPart = SettingEvidence.Unknown,
                ServerSharesSettings = SettingEvidence.No, Compatible = false,
            });
            Assert.Equal(SettingRowState.Editable, result.State);
            Assert.Equal("", result.Code);
        }
    }

    [Theory]
    [InlineData(0, "not-connected", "Not connected to a server.")]
    [InlineData(1, "waiting", "Waiting for the server…")]
    [InlineData(2, "not-progenitor", "Server doesn't run Progenitor.")]
    public void Server_owned_rows_gray_with_the_connection_reason(int connectionValue, string code, string reason)
    {
        var connection = (SettingConnection)connectionValue;
        foreach (var scope in new[] { SettingScope.Personal, SettingScope.ServerWide })
        {
            var result = Decide(scope, SettingOwner.Server, f => f with { Connection = connection, CanEditServerWide = true });
            Assert.Equal(SettingRowState.Grayed, result.State);
            Assert.Equal(code, result.Code);
            Assert.Equal(reason, result.Reason);
        }
    }

    [Fact]
    public void Reasons_come_worst_first()
    {
        // Everything wrong at once: the connection reason wins.
        var all = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with
        {
            Connection = SettingConnection.NotProgenitor, ServerSharesSettings = SettingEvidence.No, ManifestArrived = false,
            ServerPart = SettingEvidence.No, Compatible = false, AuthorEnabled = false,
        });
        Assert.Equal(SettingAvailability.NotProgenitorCode, all.Code);

        var tooOld = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with
            { ServerSharesSettings = SettingEvidence.No, ManifestArrived = false, ServerPart = SettingEvidence.No });
        Assert.Equal(SettingAvailability.ServerTooOldCode, tooOld.Code);
        Assert.Equal("Server's Progenitor can't share settings yet.", tooOld.Reason);

        var noManifest = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with
            { ServerSharesSettings = SettingEvidence.Unknown, ManifestArrived = false, ServerPart = SettingEvidence.No });
        Assert.Equal(SettingAvailability.WaitingCode, noManifest.Code);

        var absent = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with { ServerPart = SettingEvidence.No, Compatible = false });
        Assert.Equal(SettingAvailability.NeedsServerPartCode, absent.Code);
        Assert.Equal("Needs the server part of this mod.", absent.Reason);

        var mismatch = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with { Compatible = false, AuthorEnabled = false });
        Assert.Equal(SettingAvailability.VersionMismatchCode, mismatch.Code);
        Assert.Equal("Server runs a different version of this mod.", mismatch.Reason);

        var author = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with { AuthorEnabled = false, CanEditServerWide = false });
        Assert.Equal(SettingAvailability.AuthorCode, author.Code);
    }

    [Fact]
    public void A_manifest_that_does_not_name_the_mod_proves_the_server_part_absent()
    {
        var result = Decide(SettingScope.Personal, SettingOwner.Server, f => f with { ServerPart = SettingEvidence.Unknown });
        Assert.Equal(SettingAvailability.NeedsServerPartCode, result.Code);
    }

    [Fact]
    public void Server_wide_is_admin_only_for_others_and_editable_for_admins()
    {
        var other = Decide(SettingScope.ServerWide, SettingOwner.Server);
        Assert.Equal(SettingRowState.AdminOnly, other.State);
        Assert.Equal("admin-only", other.Code);
        Assert.Equal("Only server admins can change this.", other.Reason);
        Assert.False(other.Editable);

        var admin = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with { CanEditServerWide = true });
        Assert.True(admin.Editable);

        // Personal server-owned settings never need admin.
        Assert.True(Decide(SettingScope.Personal, SettingOwner.Server).Editable);
    }

    [Fact]
    public void A_server_wide_row_declared_by_the_client_half_is_still_server_owned()
    {
        var result = Decide(SettingScope.ServerWide, SettingOwner.Client, f => f with { Connection = SettingConnection.NotConnected });
        Assert.Equal(SettingAvailability.NotConnectedCode, result.Code);
    }

    [Theory]
    [InlineData(0, 0, "not-connected")]
    [InlineData(1, 0, "waiting")]
    [InlineData(2, 0, "not-progenitor")]
    [InlineData(3, 0, "needs-server-part")]
    [InlineData(3, 2, "needs-server-part")]
    [InlineData(3, 1, "")]
    public void A_client_owned_needs_server_part_row_grays_until_the_part_is_known(int connectionValue, int partValue, string code)
    {
        var connection = (SettingConnection)connectionValue;
        var part = (SettingEvidence)partValue;
        var result = Decide(SettingScope.Personal, SettingOwner.Client, f => f with { Connection = connection, ServerPart = part, NeedsServerPart = true });
        Assert.Equal(code, result.Code);
        Assert.Equal(code.Length == 0 ? SettingRowState.Editable : SettingRowState.Grayed, result.State);
    }

    [Fact]
    public void The_author_rule_grays_with_the_authors_sentence_or_a_default()
    {
        var custom = Decide(SettingScope.Personal, SettingOwner.Client, f => f with { AuthorEnabled = false, AuthorReason = "  Turn on announcements first. " });
        Assert.Equal(SettingRowState.Grayed, custom.State);
        Assert.Equal("Turn on announcements first.", custom.Reason);

        var plain = Decide(SettingScope.Personal, SettingOwner.Client, f => f with { AuthorEnabled = false });
        Assert.Equal("Not available right now.", plain.Reason);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Hide_when_alone_hides_only_for_an_absent_half(int connectionValue, bool hidden)
    {
        var connection = (SettingConnection)connectionValue;
        var result = Decide(SettingScope.Personal, SettingOwner.Server, f => f with { Connection = connection, WhenAlone = AloneBehavior.Hide });
        Assert.Equal(hidden ? SettingRowState.Hidden : SettingRowState.Grayed, result.State);
    }

    [Fact]
    public void Hide_when_alone_covers_a_missing_server_part_but_not_a_version_mismatch_or_the_author_rule()
    {
        Assert.Equal(SettingRowState.Hidden, Decide(SettingScope.ServerWide, SettingOwner.Server,
            f => f with { ServerPart = SettingEvidence.No, WhenAlone = AloneBehavior.Hide }).State);
        Assert.Equal(SettingRowState.Hidden, Decide(SettingScope.ServerWide, SettingOwner.Server,
            f => f with { ServerSharesSettings = SettingEvidence.No, WhenAlone = AloneBehavior.Hide }).State);
        Assert.Equal(SettingRowState.Grayed, Decide(SettingScope.ServerWide, SettingOwner.Server,
            f => f with { Compatible = false, WhenAlone = AloneBehavior.Hide }).State);
        Assert.Equal(SettingRowState.Grayed, Decide(SettingScope.Personal, SettingOwner.Client,
            f => f with { AuthorEnabled = false, WhenAlone = AloneBehavior.Hide }).State);
        Assert.Equal(SettingRowState.Hidden, Decide(SettingScope.Personal, SettingOwner.Client,
            f => f with { NeedsServerPart = true, ServerPart = SettingEvidence.No, WhenAlone = AloneBehavior.Hide }).State);
    }

    [Fact]
    public void A_false_visible_when_rule_hides_the_row_first()
    {
        var result = Decide(SettingScope.ServerWide, SettingOwner.Server, f => f with { AuthorVisible = false, Connection = SettingConnection.NotConnected });
        Assert.Equal(SettingRowState.Hidden, result.State);
        Assert.Equal("hidden", result.Code);
    }

    [Fact]
    public void Every_code_has_its_sentence()
    {
        foreach (var code in new[] { "not-connected", "waiting", "not-progenitor", "server-too-old", "needs-server-part", "version-mismatch", "author", "admin-only" })
            Assert.NotEqual("", SettingAvailability.ReasonFor(code));
        Assert.Equal("", SettingAvailability.ReasonFor("bogus"));
    }

    // ---- two sections -----------------------------------------------------------------------------

    private static ScopedRow Row(string id, SettingScope scope, string section, SettingRowState state = SettingRowState.Editable) =>
        new(id, scope, section, state);

    [Fact]
    public void Rows_split_into_your_settings_then_server_settings_with_author_subheadings_in_order()
    {
        var sections = SettingScopeSections.Group(new[]
        {
            Row("a", SettingScope.ServerWide, "Announcements"),
            Row("b", SettingScope.Personal, "Display"),
            Row("c", SettingScope.Personal, "Keys"),
            Row("d", SettingScope.Personal, "display"),
            Row("e", SettingScope.ServerWide, "Limits"),
        }, "Applies to everyone on Test");

        Assert.Equal(2, sections.Count);
        Assert.Equal("Your settings", sections[0].Heading);
        Assert.Equal("", sections[0].Line);
        Assert.Equal(new[] { "Display", "Keys" }, sections[0].Subsections.Select(s => s.Title));
        Assert.Equal(new[] { "b", "d" }, sections[0].Subsections[0].Rows.Select(r => r.Id));
        Assert.Equal("Server settings", sections[1].Heading);
        Assert.Equal("Applies to everyone on Test", sections[1].Line);
        Assert.Equal(new[] { "Announcements", "Limits" }, sections[1].Subsections.Select(s => s.Title));
    }

    [Fact]
    public void Hidden_rows_drop_and_empty_sections_are_omitted()
    {
        var sections = SettingScopeSections.Group(new[]
        {
            Row("a", SettingScope.ServerWide, "Limits", SettingRowState.Hidden),
            Row("b", SettingScope.Personal, "Display", SettingRowState.Grayed),
            Row("c", SettingScope.Personal, "Other", SettingRowState.Hidden),
        }, "line");
        var only = Assert.Single(sections);
        Assert.Equal("Your settings", only.Heading);
        Assert.Equal(new[] { "Display" }, only.Subsections.Select(s => s.Title));

        Assert.Empty(SettingScopeSections.Group(Array.Empty<ScopedRow>(), "line"));
        var serverOnly = Assert.Single(SettingScopeSections.Group(new[] { Row("x", SettingScope.ServerWide, "", SettingRowState.AdminOnly) }, "line"));
        Assert.Equal("Server settings", serverOnly.Heading);
    }

    [Fact]
    public void Section_titles_that_restate_the_scope_draw_no_subheading()
    {
        var sections = SettingScopeSections.Group(new[]
        {
            Row("hints", SettingScope.Personal, "Personal"),
            Row("notes", SettingScope.Personal, ""),
            Row("limit", SettingScope.ServerWide, "Server"),
            Row("demo", SettingScope.Personal, "Demo"),
        }, "line");
        Assert.Equal(new[] { "", "Demo" }, sections[0].Subsections.Select(s => s.Title));
        Assert.Equal(new[] { "hints", "notes" }, sections[0].Subsections[0].Rows.Select(r => r.Id));
        Assert.Equal(new[] { "" }, sections[1].Subsections.Select(s => s.Title));
    }

    [Fact]
    public void The_server_line_names_the_server_or_its_address_and_says_so_when_not_connected()
    {
        Assert.Equal("Applies to everyone on My Castle", SettingScopeSections.ServerLine(true, "My Castle", "1.2.3.4:9876"));
        Assert.Equal("Applies to everyone on 1.2.3.4:9876", SettingScopeSections.ServerLine(true, "", "1.2.3.4:9876"));
        Assert.Equal("Applies to everyone on the connected server", SettingScopeSections.ServerLine(true, null, null));
        Assert.Equal("Applies to everyone on the server you join", SettingScopeSections.ServerLine(false, "My Castle", "1.2.3.4:9876"));
        string longName = new string('n', 80);
        string line = SettingScopeSections.ServerLine(true, longName, "");
        Assert.Equal("Applies to everyone on " + longName, line);
    }
}
