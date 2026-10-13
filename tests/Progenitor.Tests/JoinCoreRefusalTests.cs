using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-13: the server refused a client for its Progenitor (no mod row failed), and the join screens read "All
/// ready" over a green list before the kick. These pin the refused state: the kick reason's Core requirements are found,
/// the screens never say "All ready", they say in the approved wording that the player's Progenitor is out of date (or that
/// the server's setup is the problem), the mod rows' readiness is unchanged, and Update Progenitor is offered only when
/// the self-update can fix it.
/// </summary>
public sealed class JoinCoreRefusalTests
{
    private const string A = "team.alpha";
    private const string B = "team.beta";

    // The kick reason the client logged on 2026-10-13, from a server before the versioned wording.
    private const string ObservedLegacy =
        "Mod mismatch: This server requires catalog-v2 equipment-stat synchronization. Update Progenitor Core on the client.; "
        + "This server requires catalog-v1 equipment-stat synchronization. Update Progenitor Core on the client.";

    // The same refusal from a server that names its version.
    private static readonly string Versioned = Handshake.Compare.MismatchKickReason(new List<string>
    {
        Handshake.Compare.CoreCapabilityProblem("catalog-v2 equipment-stat synchronization", "2026.10.195"),
        Handshake.Compare.CoreCapabilityProblem("catalog-v1 equipment-stat synchronization", "2026.10.195"),
    });

    private static readonly string ItemLimit =
        Handshake.Compare.MismatchKickReason(new List<string> { Handshake.Compare.ItemBudgetProblem(1024, "2026.10.195") });

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static JoinPageModel Refused(string kickReason, Handshake.ModRecord[] asked, Handshake.ModRecord[] provided,
        string own = "2026.10.191", string? update = null) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = asked,
            Provided = provided,
            Refused = true,
            CoreRequirements = JoinCoreRefusal.Parse(kickReason),
            OwnCoreVersion = own,
            UpdateVersion = update,
        });

    private sealed class FakeEffects : IJoinFlowEffects
    {
        public int Updates;
        public bool Starts = true;
        public bool CopyToClipboard(string text) => true;
        public void Leave() { }
        public void Closed(bool completed) { }
        public void MarkSeen() { }
        public void PageShown(JoinPage page) { }

        public string UpdateProgenitor(out bool started)
        {
            Updates++;
            started = Starts;
            return Starts ? JoinText.UpdateStarted("2026.10.196") : "Progenitor couldn't be updated here (busy). Update it from the Mods menu, then join again.";
        }
    }

    // ---------------------------------------------------------------- the server's wording

    [Fact]
    public void The_versioned_server_wording_round_trips_with_the_servers_version()
    {
        Assert.Equal("Mod mismatch: Needs Progenitor 2026.10.195 or newer (catalog-v2 equipment-stat synchronization). "
            + "Update Progenitor Core on the client.; Needs Progenitor 2026.10.195 or newer (catalog-v1 equipment-stat synchronization). "
            + "Update Progenitor Core on the client.", Versioned);
        var reqs = JoinCoreRefusal.Parse(Versioned);
        Assert.Equal(new[] { "catalog-v2 equipment-stat synchronization", "catalog-v1 equipment-stat synchronization" },
            reqs.Select(r => r.Capability).ToArray());
        Assert.All(reqs, r => Assert.Equal("2026.10.195", r.ServerCore));
        Assert.All(reqs, r => Assert.Equal(JoinCoreRequirementKind.Capability, r.Kind));
    }

    [Fact]
    public void An_older_servers_wording_is_still_read_without_a_version()
    {
        var reqs = JoinCoreRefusal.Parse(ObservedLegacy);
        Assert.Equal(new[] { "catalog-v2 equipment-stat synchronization", "catalog-v1 equipment-stat synchronization" },
            reqs.Select(r => r.Capability).ToArray());
        Assert.All(reqs, r => Assert.Equal("", r.ServerCore));
        Assert.Equal("Needs a newer Progenitor (x). Update Progenitor Core on the client.",
            Handshake.Compare.CoreCapabilityProblem("x", null));
        Assert.Equal("x", Assert.Single(JoinCoreRefusal.Parse(Handshake.Compare.CoreCapabilityProblem("x", "")))!.Capability);
    }

    [Fact]
    public void Mod_row_problems_a_note_duplicates_and_cut_problems_are_not_core_requirements()
    {
        Assert.Empty(JoinCoreRefusal.Parse(null));
        Assert.Empty(JoinCoreRefusal.Parse(""));
        Assert.Empty(JoinCoreRefusal.Parse("Mod mismatch: team.alpha missing (server requires 1.0)"));
        string one = Handshake.Compare.CoreCapabilityProblem("extended item synchronization", "2026.10.195");
        var reqs = JoinCoreRefusal.Parse("Mod mismatch: a note | " + one + "; " + one + "; team.alpha missing");
        Assert.Equal("extended item synchronization", Assert.Single(reqs).Capability);
        // A problem cut short by the kick reason's length cap is not claimed.
        Assert.Empty(JoinCoreRefusal.Parse("Mod mismatch: " + one.Substring(0, one.Length - 10)));
    }

    [Fact]
    public void The_item_limit_problem_carries_the_limit_and_older_servers_wording_is_read()
    {
        Assert.Equal("Mod mismatch: Needs Progenitor set to accept 1024 custom items (this server runs 2026.10.195). "
            + "Set Networking.MaxSyncedPrefabsPerJoin to 1024 on a current Progenitor client.", ItemLimit);
        var req = Assert.Single(JoinCoreRefusal.Parse(ItemLimit));
        Assert.Equal(JoinCoreRequirementKind.ItemBudget, req.Kind);
        Assert.Equal(1024, req.Limit);
        var legacy = Assert.Single(JoinCoreRefusal.Parse("Mod mismatch: " + Handshake.Compare.LegacyItemBudgetHead
            + " Set Networking.MaxSyncedPrefabsPerJoin to 16384 on a current Progenitor client."));
        Assert.Equal(16384, legacy.Limit);
    }

    // ---------------------------------------------------------------- the screens' words (approved wording 2026-10-13)

    [Fact]
    public void An_out_of_date_Progenitor_reads_as_such_with_both_versions_and_no_feature_names()
    {
        var mods = new[] { Must(A, "1"), Must(B, "2") };
        var model = Refused(Versioned, mods, mods);

        Assert.Equal(2, model.RequiredReady);
        Assert.Equal("2 of 2 ready", JoinText.ReadyCount(model));
        Assert.True(model.CoreRefused);
        Assert.False(model.AllRequiredReady);
        Assert.Equal("Your Progenitor is out of date", JoinText.Summary(model));
        Assert.Equal("Your Progenitor is out of date", JoinText.CoreRefusedTitle(model.Facts.CoreRequirements));
        string body = JoinText.CoreRefusedBody(model.Facts.CoreRequirements, model.Facts.OwnCoreVersion);
        Assert.Equal("This server needs Progenitor 2026.10.195. You have 2026.10.191. Update Progenitor, then join again.", body);
        Assert.DoesNotContain("synchronization", body, StringComparison.Ordinal);
        // The feature names stay for the log and the readback.
        Assert.Equal("missing catalog-v2 equipment-stat synchronization; missing catalog-v1 equipment-stat synchronization",
            JoinText.CoreRefusedDetail(model.Facts.CoreRequirements));
    }

    [Fact]
    public void Without_the_servers_version_the_body_still_says_to_update()
    {
        var model = Refused(ObservedLegacy, Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>());
        Assert.Equal("Your Progenitor is out of date", JoinText.Summary(model));
        Assert.Equal("This server needs a newer Progenitor. You have 2026.10.191. Update Progenitor, then join again.",
            JoinText.CoreRefusedBody(model.Facts.CoreRequirements, model.Facts.OwnCoreVersion));
        Assert.Equal("This server needs a newer Progenitor. Update Progenitor, then join again.",
            JoinText.CoreRefusedBody(model.Facts.CoreRequirements, ""));
    }

    [Fact]
    public void The_item_limit_is_the_servers_setup_for_its_admins_to_fix()
    {
        var model = Refused(ItemLimit, new[] { Must(A, "1") }, new[] { Must(A, "1") }, own: "2026.10.195", update: "2026.10.196");
        Assert.Equal("This server's setup isn't compatible with your game", JoinText.Summary(model));
        Assert.Equal("Its admins need to fix a Progenitor setting before you can join. Let them know.",
            JoinText.CoreRefusedBody(model.Facts.CoreRequirements, model.Facts.OwnCoreVersion));
        Assert.Equal("item limit 1024 (Networking.MaxSyncedPrefabsPerJoin)", JoinText.CoreRefusedDetail(model.Facts.CoreRequirements));
        // Updating Progenitor does not fix a server setting.
        Assert.False(model.CanUpdateProgenitor);
    }

    [Fact]
    public void A_refusal_over_mod_rows_alone_is_unchanged()
    {
        var model = Refused("Mod mismatch: team.alpha missing", new[] { Must(A, "1") }, new[] { Must(A, "1") }, update: "2026.10.196");
        Assert.False(model.CoreRefused);
        Assert.False(model.CanUpdateProgenitor);
        Assert.Equal("", JoinText.CoreRefusedTitle(model.Facts.CoreRequirements));
        Assert.Equal("", JoinText.CoreRefusedBody(model.Facts.CoreRequirements, "2026.10.191"));
        Assert.Equal("1 required mod. All ready.", JoinText.Summary(model));
    }

    // ---------------------------------------------------------------- Update Progenitor

    [Theory]
    [InlineData("2026.10.196", true)]   // newer than the server's: fixes it
    [InlineData("2026.10.195", true)]   // the server's own release
    [InlineData("2026.10.194", false)]  // newer than mine, still older than the server's
    [InlineData("2026.10.191", false)]  // what I already run
    [InlineData(null, false)]           // nothing the self-update can install here
    public void Update_Progenitor_is_offered_only_for_a_release_that_fixes_the_refusal(string? offered, bool expected)
    {
        var mods = new[] { Must(A, "1") };
        Assert.Equal(expected, Refused(Versioned, mods, mods, update: offered).CanUpdateProgenitor);
    }

    [Fact]
    public void Without_the_servers_version_any_newer_release_is_offered()
    {
        var mods = new[] { Must(A, "1") };
        Assert.True(Refused(ObservedLegacy, mods, mods, update: "2026.10.192").CanUpdateProgenitor);
        Assert.False(Refused(ObservedLegacy, mods, mods, update: "2026.10.191").CanUpdateProgenitor);
    }

    [Fact]
    public void Update_Progenitor_is_the_main_button_on_both_pages_and_starts_the_update_once()
    {
        var mods = new[] { Must(A, "1") };
        var fx = new FakeEffects();
        var s = new JoinFlowSession(JoinMode.Refused, Refused(Versioned, mods, mods, update: "2026.10.196"), fx);
        Assert.True(s.ModsPageDue);
        Assert.True(s.OffersUpdateProgenitor);

        var modsButtons = JoinFlowLayout.ModsButtons(s);
        Assert.Equal(new[] { JoinText.CloseButton, JoinText.UpdateProgenitorButton }, modsButtons.Select(b => b.Caption).ToArray());
        Assert.Equal(DialogButtonRole.Main, modsButtons.Single(b => b.Key == JoinFlowLayout.ButtonUpdateProgenitor).Role);
        var messageButtons = JoinFlowLayout.MessageButtons(s);
        Assert.Equal(JoinText.UpdateProgenitorButton, messageButtons.Last().Caption);
        Assert.Equal(DialogButtonRole.Main, messageButtons.Last().Role);
        Assert.Equal(DialogButtonRole.Option, messageButtons.Single(b => b.Key == JoinFlowLayout.ButtonContinue).Role);

        Assert.Equal("update-progenitor", s.UpdateProgenitor());
        Assert.Equal(1, fx.Updates);
        Assert.True(s.NoticeGood);
        Assert.Equal("Downloading Progenitor 2026.10.196. It's installed when you're back at the title screen. Then join again.", s.Notice);
        // Started once: the button goes and a second press does nothing.
        Assert.False(s.OffersUpdateProgenitor);
        Assert.DoesNotContain(JoinFlowLayout.ModsButtons(s), b => b.Key == JoinFlowLayout.ButtonUpdateProgenitor);
        Assert.Equal("not offered", s.UpdateProgenitor());
        Assert.Equal(1, fx.Updates);
    }

    [Fact]
    public void An_update_that_cannot_start_says_so_and_keeps_the_button()
    {
        var mods = new[] { Must(A, "1") };
        var fx = new FakeEffects { Starts = false };
        var s = new JoinFlowSession(JoinMode.Refused, Refused(Versioned, mods, mods, update: "2026.10.196"), fx);
        Assert.Equal("update-progenitor (not started)", s.UpdateProgenitor());
        Assert.False(s.NoticeGood);
        Assert.StartsWith("Progenitor couldn't be updated here", s.Notice, StringComparison.Ordinal);
        Assert.True(s.OffersUpdateProgenitor);
    }

    [Fact]
    public void Without_an_update_the_mods_page_offers_close_only()
    {
        var mods = new[] { Must(A, "1") };
        var s = new JoinFlowSession(JoinMode.Refused, Refused(Versioned, mods, mods), new FakeEffects());
        Assert.False(s.OffersUpdateProgenitor);
        Assert.Equal(new[] { JoinText.CloseButton }, JoinFlowLayout.ModsButtons(s).Select(b => b.Caption).ToArray());
        Assert.Equal(DialogButtonRole.Main, JoinFlowLayout.MessageButtons(s).Single(b => b.Key == JoinFlowLayout.ButtonContinue).Role);
    }
}
