using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3, the <b>Version matching</b> admin setting (docs/client-mod-policy.md, "Version matching"): the
/// store keys <c>versionMatching</c> and <c>serverModRules</c> in <c>progenitor-join-admin.json</c>, the panel's staged
/// edits and their words, the state on the wire, the Server tab row, the setting's place and lock id. Game-free; the
/// server half (requests, cfg override, lock, the judge's source) is <see cref="VersionMatchingServerTests"/>.
/// </summary>
public sealed class VersionMatchingSettingTests
{
    // ---- the store -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_older_file_loads_with_range_and_no_rules_and_the_next_write_adds_both_keys()
    {
        Assert.True(JoinAdminConfig.TryParse("{\"schema\":2,\"recommended\":[],\"clientPolicy\":{\"mode\":\"any\"}}", out var v2, out var error));
        Assert.Null(error);
        Assert.Equal(VersionMatchingMode.Range, v2.VersionMatching);
        Assert.Empty(v2.ServerModRules);
        Assert.True(JoinAdminConfig.TryParse("{\"schema\":1}", out var v1, out _));
        Assert.Equal(VersionMatchingMode.Range, v1.VersionMatching);

        string json = new JoinAdminConfig().ToJson();
        Assert.Contains("\"versionMatching\": \"range\"", json);
        Assert.Contains("\"serverModRules\": []", json);
        Assert.Contains("\"schema\": 2", json);
    }

    [Fact]
    public void The_mode_and_the_rules_round_trip_and_clone()
    {
        var config = new JoinAdminConfig { VersionMatching = VersionMatchingMode.Exact };
        config.ServerModRules.Add(new ServerModRule("acme.chatskin", ClientModRule.Any, "ChatSkin"));
        config.ServerModRules.Add(new ServerModRule("acme.maps", ClientModRule.Minor));
        string json = config.ToJson();
        Assert.Contains("\"versionMatching\": \"exact\"", json);
        Assert.True(JoinAdminConfig.TryParse(json, out var again, out _));
        Assert.Equal(VersionMatchingMode.Exact, again.VersionMatching);
        Assert.Equal(new[] { "acme.chatskin", "acme.maps" }, again.ServerModRules.Select(r => r.Id));
        Assert.Equal(new[] { ClientModRule.Any, ClientModRule.Minor }, again.ServerModRules.Select(r => r.Rule));
        Assert.Equal("ChatSkin", again.ServerModRules[0].Name);
        Assert.Equal("", again.ServerModRules[1].Name);

        var copy = again.Clone();
        Assert.Equal(VersionMatchingMode.Exact, copy.VersionMatching);
        copy.ServerModRules.Clear();
        Assert.Equal(2, again.ServerModRules.Count);
        Assert.Equal(1, again.IndexOfServerModRule("ACME.MAPS"));
        Assert.Equal(-1, again.IndexOfServerModRule("other"));
    }

    [Fact]
    public void Malformed_rows_and_an_unknown_mode_are_skipped_with_a_line_each()
    {
        var rows = new List<string>();
        for (int i = 0; i < VersionMatchLimits.MaxServerModRules + 1; i++) rows.Add("{\"id\":\"m" + i + "\",\"rule\":\"exact\"}");
        string json = "{\"schema\":2,\"versionMatching\":\"strict\",\"serverModRules\":["
                      + "{\"id\":\"a.mod\",\"rule\":\"sometimes\"},{\"id\":\"bad id/x\",\"rule\":\"exact\"},{\"rule\":\"exact\"},{\"id\":\"b.mod\"},"
                      + "{\"id\":\"c.mod\",\"rule\":\"exact\"},{\"id\":\"C.MOD\",\"rule\":\"any\"},7,"
                      + string.Join(",", rows) + "]}";
        var skipped = new List<string>();
        Assert.True(JoinAdminConfig.TryParse(json, out var config, out var error, skipped));
        Assert.Null(error);
        Assert.Equal(VersionMatchingMode.Range, config.VersionMatching);
        Assert.Contains(skipped, s => s.Contains("versionMatching \"strict\"") && s.Contains("Use each mod's declared range kept"));
        Assert.Contains(skipped, s => s.StartsWith("serverModRules a.mod: unknown rule \"sometimes\"", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.StartsWith("serverModRules bad id/x: the mod id is not valid", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s == "serverModRules (no id): no id");
        Assert.Contains(skipped, s => s == "serverModRules b.mod: unknown rule \"\"");
        Assert.Contains(skipped, s => s == "serverModRules C.MOD: listed twice");
        Assert.Contains(skipped, s => s.Contains("more than " + VersionMatchLimits.MaxServerModRules + " server mod rules"));
        Assert.Equal(VersionMatchLimits.MaxServerModRules, config.ServerModRules.Count);
        Assert.Equal("c.mod", config.ServerModRules[0].Id);
    }

    // ---- the decisions (the server half calls the same code after authorization) ------------------------------------

    private static JoinAdminDecision Decide(JoinAdminConfig config, JoinAdminRequest r, VersionMatchingMode? forced = null) =>
        JoinAdminPolicy.Decide(config, r, Array.Empty<string>(), null, 60, new[] { "acme.chatskin", "acme.maps" }, forced);

    [Fact]
    public void Version_matching_decisions_store_refuse_and_say_unchanged()
    {
        var empty = new JoinAdminConfig();
        var d = Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = "exact" });
        Assert.True(d.Ok);
        Assert.True(d.MatchingChanged);
        Assert.True(d.Changed);
        Assert.False(d.PolicyChanged);
        Assert.Equal(VersionMatchingMode.Exact, d.Config.VersionMatching);
        Assert.Equal(VersionMatchingMode.Range, empty.VersionMatching);
        Assert.Equal("progenitor/JoinAdmin/version-matching", d.AuditRow);
        Assert.Equal(("range", "exact"), (d.Before, d.After));
        Assert.Equal(VersionMatchText.NextJoinNote, d.Note);

        var same = Decide(d.Config, new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = "Exact version for all" });
        Assert.True(same.Ok);
        Assert.False(same.Changed);
        Assert.Equal("Unchanged.", same.Note);

        var bad = Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = "strict" });
        Assert.False(bad.Ok);
        Assert.Equal("Choose range, minor or exact.", bad.Error);

        var forced = Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = "minor" }, VersionMatchingMode.Exact);
        Assert.False(forced.Ok);
        Assert.Equal("Set in the server's config file ([ClientPolicy] VersionMatching = exact).", forced.Error);
    }

    [Fact]
    public void A_per_mod_rule_is_only_for_a_mod_the_server_runs_and_default_clears_it()
    {
        var empty = new JoinAdminConfig();
        var set = Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "acme.chatskin", Rule = "any", Name = "ChatSkin" });
        Assert.True(set.Ok);
        Assert.True(set.MatchingChanged);
        Assert.Equal("progenitor/JoinAdmin/server-mod-rule/acme.chatskin", set.AuditRow);
        Assert.Equal(("default", "any"), (set.Before, set.After));
        var stored = Assert.Single(set.Config.ServerModRules);
        Assert.Equal(("acme.chatskin", ClientModRule.Any, "ChatSkin"), (stored.Id, stored.Rule, stored.Name));

        // Changing it keeps the stored name; the same rule again is unchanged.
        var exact = Decide(set.Config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "ACME.ChatSkin", Rule = "exact" });
        Assert.True(exact.Ok);
        Assert.Equal(("any", "exact"), (exact.Before, exact.After));
        Assert.Equal("ChatSkin", Assert.Single(exact.Config.ServerModRules).Name);
        Assert.Equal("Unchanged.", Decide(exact.Config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "acme.chatskin", Rule = "exact" }).Note);

        // default (or no rule) clears it; clearing nothing is unchanged.
        var cleared = Decide(exact.Config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "acme.chatskin", Rule = "default" });
        Assert.True(cleared.Ok);
        Assert.Empty(cleared.Config.ServerModRules);
        Assert.Equal(("exact", "default"), (cleared.Before, cleared.After));
        var nothing = Decide(cleared.Config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "acme.chatskin" });
        Assert.True(nothing.Ok);
        Assert.False(nothing.Changed);

        // Refusals: a mod the server doesn't run, Progenitor, a bad id or rule.
        Assert.Equal(VersionMatchText.NotRunError, Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "client.only", Rule = "any" }).Error);
        Assert.Equal(VersionMatchText.ProgenitorError, Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "progenitor", Rule = "any" }).Error);
        Assert.StartsWith("The mod id is not valid", Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "a/b", Rule = "any" }).Error);
        Assert.Equal("Choose a rule: exact, at-least, minor, any or default.",
            Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "acme.maps", Rule = "maybe" }).Error);
        Assert.Equal("Name the mod (its plugin id).", Decide(empty, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Rule = "any" }).Error);

        // A stored rule for a mod the server stopped running can still be cleared.
        var stale = new JoinAdminConfig();
        stale.ServerModRules.Add(new ServerModRule("gone.mod", ClientModRule.Exact));
        Assert.True(Decide(stale, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "gone.mod", Rule = "default" }).Ok);
    }

    [Fact]
    public void At_most_64_mods_have_their_own_rule()
    {
        var config = new JoinAdminConfig();
        var runs = new List<string>();
        for (int i = 0; i <= VersionMatchLimits.MaxServerModRules; i++) runs.Add("m" + i);
        for (int i = 0; i < VersionMatchLimits.MaxServerModRules; i++) config.ServerModRules.Add(new ServerModRule("m" + i, ClientModRule.Any));
        var full = JoinAdminPolicy.Decide(config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "m64", Rule = "any" },
            Array.Empty<string>(), null, 60, runs);
        Assert.False(full.Ok);
        Assert.Equal("At most 64 mods can have their own rule.", full.Error);
        // Changing an existing one is still fine.
        Assert.True(JoinAdminPolicy.Decide(config, new JoinAdminRequest { Op = JoinAdminOps.ServerModRule, Id = "m3", Rule = "exact" },
            Array.Empty<string>(), null, 60, runs).Ok);
    }

    // ---- the judge's settings ----------------------------------------------------------------------------------------

    [Fact]
    public void The_settings_are_the_stored_mode_unless_the_cfg_forces_one_and_carry_the_rules()
    {
        var config = new JoinAdminConfig { VersionMatching = VersionMatchingMode.Minor };
        config.ServerModRules.Add(new ServerModRule("acme.chatskin", ClientModRule.Any));
        var built = VersionMatchSetting.Build(config, null);
        Assert.Equal(VersionMatchingMode.Minor, built.Mode);
        Assert.True(built.TryRule("ACME.CHATSKIN", out var rule));
        Assert.Equal(ClientModRule.Any, rule);
        Assert.Equal("minor (1 mod rule)", built.Describe());

        var forced = VersionMatchSetting.Build(config, VersionMatchingMode.Exact);
        Assert.Equal(VersionMatchingMode.Exact, forced.Mode);
        Assert.True(forced.TryRule("acme.chatskin", out _));
        Assert.Equal(VersionMatchingMode.Range, VersionMatchSetting.Build(null, null).Mode);

        // The per-mod rule wins in the judge: Exact for all, but Any version for this one mod.
        var row = new Handshake.ModRecord("acme.chatskin", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, declared: true);
        Assert.Null(VersionMatchJudge.Check(row, "1.4.0", new VersionJudgeContext { Settings = forced }));
        var other = new Handshake.ModRecord("acme.maps", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, declared: true);
        Assert.Equal("acme.maps: this server requires exactly 1.2.0 (you have 1.2.1)", VersionMatchJudge.Check(other, "1.2.1", new VersionJudgeContext { Settings = forced }));
    }

    [Fact]
    public void The_cfg_override_reads_the_three_words_and_warns_once_about_anything_else()
    {
        var warned = new List<string>();
        try
        {
            VersionMatchCfg.Warn = warned.Add;
            Assert.Null(VersionMatchCfg.Forced(""));
            Assert.Null(VersionMatchCfg.Forced("  "));
            Assert.Equal(VersionMatchingMode.Exact, VersionMatchCfg.Forced("exact"));
            Assert.Equal(VersionMatchingMode.Minor, VersionMatchCfg.Forced(" Minor "));
            Assert.Equal(VersionMatchingMode.Range, VersionMatchCfg.Forced("range"));
            Assert.Null(VersionMatchCfg.Forced("strictest"));
            Assert.Null(VersionMatchCfg.Forced("strictest"));
            Assert.Equal("[ClientPolicy] VersionMatching = 'strictest' is not range, minor or exact; the admins' choice applies.", Assert.Single(warned));
            Assert.Null(VersionMatchCfg.ForcedNow());
            VersionMatchCfg.Source = () => "minor";
            Assert.Equal(VersionMatchingMode.Minor, VersionMatchCfg.ForcedNow());
            Assert.Equal("Set in the server's config file ([ClientPolicy] VersionMatching = minor).", VersionMatchCfg.ForcedNote(VersionMatchingMode.Minor));
        }
        finally { VersionMatchCfg.Reset(); }
    }

    // ---- the words -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_Rule_button_cycles_through_the_server_setting_and_the_four_rules()
    {
        Assert.Equal("exact", VersionMatchText.NextRule(""));
        Assert.Equal("at-least", VersionMatchText.NextRule("exact"));
        Assert.Equal("minor", VersionMatchText.NextRule("at-least"));
        Assert.Equal("any", VersionMatchText.NextRule("minor"));
        Assert.Equal("", VersionMatchText.NextRule("any"));
        Assert.Equal("exact", VersionMatchText.NextRule("default"));

        Assert.Equal("Use the server setting", VersionMatchText.RuleLabel("", "1.2.0"));
        Assert.Equal("Exact 1.2.0", VersionMatchText.RuleLabel("exact", "1.2.0"));
        Assert.Equal("1.2.0 or newer", VersionMatchText.RuleLabel("at-least", "1.2.0"));
        Assert.Equal("Same minor as 1.2.0", VersionMatchText.RuleLabel("minor", "1.2.0"));
        Assert.Equal("Any version", VersionMatchText.RuleLabel("any", "1.2.0"));
        Assert.Equal(new[] { "Server setting", "Exact", "Or newer", "Same minor", "Any version" },
            new[] { "", "exact", "at-least", "minor", "any" }.Select(VersionMatchText.RuleCaption));

        var row = new JoinAdminServerModRow { Id = "acme.chatskin", Name = "ChatSkin", Version = "1.2.0", Rule = "exact" };
        Assert.Equal("acme.chatskin · 1.2.0 · Exact 1.2.0", VersionMatchText.Second(row));
        Assert.Equal("Exact · not on this server now", VersionMatchText.Second(new JoinAdminServerModRow { Id = "gone.mod", Rule = "exact", Runs = false }));
        Assert.Equal("None", VersionMatchText.RulesSummary(new[] { new JoinAdminServerModRow { Id = "x" } }));
        Assert.Equal("ChatSkin: Exact 1.2.0", VersionMatchText.RulesSummary(new[] { row, new JoinAdminServerModRow { Id = "x" } }));
        Assert.Equal(new[] { "range", "minor", "exact" }, VersionMatchText.Options.Select(o => o.Value));
        Assert.Equal(VersionMatchingText.LabelExact, VersionMatchText.Options[2].Label);
        Assert.Equal(VersionMatchingText.HelpExact, VersionMatchText.Options[2].Description);
    }

    // ---- the wire --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_state_carries_the_mode_the_forced_flag_and_the_mods_and_an_older_state_has_none()
    {
        var s = new JoinAdminState { MatchingSupported = true, VersionMatching = "exact", VersionMatchingForced = true };
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "acme.chatskin", Name = "ChatSkin", Version = "1.2.0", Rule = "any" });
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "acme.maps", Version = "2.0.0" });
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "gone.mod", Rule = "exact", Runs = false });
        var bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Ok = true, Op = "get", State = s });
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var r));
        var back = r.State!;
        Assert.True(back.MatchingSupported);
        Assert.Equal("exact", back.VersionMatching);
        Assert.True(back.VersionMatchingForced);
        Assert.Equal(new[] { "acme.chatskin", "acme.maps", "gone.mod" }, back.ServerMods.Select(m => m.Id));
        Assert.Equal(new[] { "any", "", "exact" }, back.ServerMods.Select(m => m.Rule));
        Assert.Equal(new[] { true, true, false }, back.ServerMods.Select(m => m.Runs));
        Assert.Equal("ChatSkin", back.ServerMods[0].Name);
        Assert.Equal("2.0.0", back.ServerMods[1].Version);

        var old = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Ok = true, Op = "get", State = new JoinAdminState() });
        Assert.True(JoinAdminWire.TryDecodeResponse(old, out var o));
        Assert.False(o.State!.MatchingSupported);
        Assert.Equal("", o.State.VersionMatching);
    }

    [Fact]
    public void A_full_answer_drops_the_server_mods_without_a_rule_before_the_state()
    {
        var s = new JoinAdminState { MatchingSupported = true, VersionMatching = "range" };
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "keep.me", Rule = "exact" });
        for (int i = 0; i < 400; i++) s.ServerMods.Add(new JoinAdminServerModRow { Id = "plain.mod" + i + new string('x', 30), Version = "1.0.0" });
        var bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Ok = true, Op = "get", State = s });
        Assert.True(bytes.Length <= JoinAdminLimits.MaxPacketBytes);
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var r));
        Assert.Contains(r.State!.ServerMods, m => m.Id == "keep.me" && m.Rule == "exact");
    }

    [Fact]
    public void The_requests_carry_their_value_or_their_mod_rule_and_name()
    {
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = "minor" }), out var a));
        Assert.Equal((JoinAdminOps.VersionMatching, "minor"), (a.Op, a.Value));
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(new JoinAdminRequest
        {
            Op = JoinAdminOps.ServerModRule, Id = "acme.chatskin", Rule = "default", Name = "ChatSkin",
        }), out var b));
        Assert.Equal(("acme.chatskin", "default", "ChatSkin"), (b.Id, b.Rule, b.Name));
        Assert.Contains(JoinAdminOps.VersionMatching, JoinAdminOps.All);
        Assert.Contains(JoinAdminOps.ServerModRule, JoinAdminOps.All);
        Assert.True(JoinAdminOps.IsMatching(JoinAdminOps.ServerModRule));
        // Not a policy op: nobody in the world is re-judged (or disconnected) when it changes.
        Assert.False(JoinAdminOps.IsPolicy(JoinAdminOps.VersionMatching));
        Assert.False(JoinAdminOps.IsPolicy(JoinAdminOps.ServerModRule));
    }

    // ---- the panel's draft -----------------------------------------------------------------------------------------

    private static JoinAdminState Server(bool forced = false)
    {
        var s = new JoinAdminState { WindowSeconds = 60, PolicySupported = true, PolicyMode = "any", Unverified = "allow", MidSession = "warn" };
        s.MatchingSupported = true;
        s.VersionMatching = forced ? "exact" : "range";
        s.VersionMatchingForced = forced;
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "acme.chatskin", Name = "ChatSkin", Version = "1.2.0" });
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "acme.maps", Version = "2.0.0", Rule = "minor" });
        s.ServerMods.Add(new JoinAdminServerModRow { Id = "gone.mod", Rule = "exact", Runs = false });
        return s;
    }

    private static JoinAdminDraft Draft(JoinAdminState? s = null)
    {
        var d = new JoinAdminDraft();
        d.Reset(s ?? Server());
        return d;
    }

    [Fact]
    public void Version_matching_is_staged_described_and_applied_as_its_request()
    {
        var d = Draft();
        Assert.Equal("range", d.Word(JoinAdminChangeKind.VersionMatching));
        Assert.Null(d.SetVersionMatching("exact"));
        Assert.Equal("exact", d.Word(JoinAdminChangeKind.VersionMatching));
        Assert.Equal("range", d.Base!.VersionMatching);
        var c = Assert.Single(d.Changes);
        Assert.Equal("Version matching: Use each mod's declared range → Exact version for all", JoinAdminDraft.Describe(c));
        var step = Assert.Single(d.Steps());
        Assert.Equal((JoinAdminOps.VersionMatching, "exact"), (step.Request.Op, step.Request.Value));
        Assert.Equal("version-matching", JoinAdminDraft.KindName(c.Kind));
        Assert.Equal("exact", d.Effective()!.VersionMatching);

        // Back to the server's value: no change. A bad word is refused.
        Assert.Null(d.SetVersionMatching("range"));
        Assert.False(d.HasChanges);
        Assert.Equal("Choose range, minor or exact.", d.SetVersionMatching("strict"));

        // Forced by the server's cfg: read-only, with the note.
        Assert.Equal("Set in the server's config file ([ClientPolicy] VersionMatching = exact).", Draft(Server(forced: true)).SetVersionMatching("range"));
        // A server older than C3: nothing to stage.
        var old = new JoinAdminState { PolicySupported = true, PolicyMode = "any", Unverified = "allow" };
        Assert.Equal("This server does not have Version matching.", Draft(old).SetVersionMatching("exact"));
        Assert.Equal("", Draft(old).Word(JoinAdminChangeKind.VersionMatching));
    }

    [Fact]
    public void A_server_mods_rule_is_staged_marked_described_and_sent_after_the_mode()
    {
        var d = Draft();
        Assert.Null(d.SetServerModRule("acme.chatskin", "exact"));
        Assert.Null(d.SetServerModRule("acme.maps", "default"));
        Assert.Null(d.SetVersionMatching("minor"));
        var rows = d.ServerModRows();
        Assert.Equal(JoinAdminRowMark.Changed, rows[0].Mark);
        Assert.Equal("exact", rows[0].Row.Rule);
        Assert.Equal(JoinAdminRowMark.Changed, rows[1].Mark);
        Assert.Equal("", rows[1].Row.Rule);
        Assert.Equal(JoinAdminRowMark.None, rows[2].Mark);

        Assert.Equal(new[]
        {
            "ChatSkin: use the server setting → exact 1.2.0",
            "acme.maps: same minor as 2.0.0 → use the server setting",
            "Version matching: Use each mod's declared range → Same minor for all",
        }, d.Lines());
        var steps = d.Steps();
        Assert.Equal(new[] { JoinAdminOps.VersionMatching, JoinAdminOps.ServerModRule, JoinAdminOps.ServerModRule }, steps.Select(s => s.Request.Op));
        Assert.Equal(("acme.chatskin", "exact", "ChatSkin"), (steps[1].Request.Id, steps[1].Request.Rule, steps[1].Request.Name));
        Assert.Equal(("acme.maps", "default"), (steps[2].Request.Id, steps[2].Request.Rule));
        Assert.Equal("exact", d.Effective()!.ServerMods[0].Rule);

        // Back to the server's own rule: the change goes.
        Assert.Null(d.SetServerModRule("acme.maps", "minor"));
        Assert.Equal(2, d.Count);

        // Refusals: a mod the server doesn't list, a bad word, a new rule for a mod it no longer runs (clearing it is fine).
        Assert.Equal(VersionMatchText.NotRunError, d.SetServerModRule("client.only", "any"));
        Assert.StartsWith("Unknown rule", d.SetServerModRule("acme.chatskin", "maybe"));
        Assert.Equal(VersionMatchText.NotRunError, d.SetServerModRule("gone.mod", "any"));
        Assert.Null(d.SetServerModRule("gone.mod", "default"));
    }

    [Fact]
    public void Rebase_drops_what_the_server_now_has_and_flags_another_admins_rule()
    {
        var d = Draft();
        Assert.Null(d.SetServerModRule("acme.chatskin", "exact"));
        Assert.Null(d.SetServerModRule("acme.maps", "any"));
        Assert.Null(d.SetVersionMatching("exact"));

        var moved = Server();
        moved.VersionMatching = "exact";
        moved.ServerMods[0].Rule = "exact";
        moved.ServerMods[1].Rule = "at-least";
        d.Rebase(moved);
        var left = Assert.Single(d.Changes);
        Assert.Equal("acme.maps", left.Id);
        Assert.Equal("another admin changed it to 2.0.0 or newer", left.Conflict);
        Assert.Equal("at-least", left.Before);

        // The server stops listing the mod: the change goes.
        var gone = Server();
        gone.ServerMods.RemoveAt(1);
        d.Rebase(gone);
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void The_lock_list_refuses_staging_both_edits()
    {
        var d = Draft();
        d.LockedBy = op => ServerSettingsLock.JoinAdminSettingId(op) == SettingPlaces.JoinAdminId(SettingPlaces.OpVersionMatching) ? "locked" : null;
        Assert.Equal("locked", d.SetVersionMatching("exact"));
        Assert.Equal("locked", d.SetServerModRule("acme.chatskin", "exact"));
        Assert.False(d.HasChanges);
    }

    // ---- placement and lock --------------------------------------------------------------------------------------

    [Fact]
    public void The_setting_lives_in_the_popup_mirrored_under_Mods_players_need_and_locks_with_that_group()
    {
        string id = SettingPlaces.JoinAdminId(SettingPlaces.OpVersionMatching);
        Assert.Equal("progenitor/JoinAdmin/version-matching", id);
        var place = SettingPlaces.Find(id)!;
        Assert.Equal(SettingPlaces.PlayersJoiningPanel, place.Home);
        Assert.Equal(new[] { SettingPlaces.ModsPlayersNeed }, place.AlsoAt);
        Assert.Equal(SettingPlaces.StoreJoinAdmin, place.Store);
        Assert.Contains("versionMatching", place.Source);
        Assert.Contains("[ClientPolicy] VersionMatching", place.Source);
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find("progenitor/ClientPolicy/VersionMatching")!.Home);

        Assert.Equal(id, ServerSettingsLock.JoinAdminSettingId(JoinAdminOps.VersionMatching));
        Assert.Equal(id, ServerSettingsLock.JoinAdminSettingId(JoinAdminOps.ServerModRule));
        Assert.Contains(ServerSettingsLock.GroupModsPlayersNeed, ServerSettingsLock.GroupsOf(id));
        Assert.Equal("mods-players-need", ServerSettingsLock.Parse("mods-players-need").Match(id));
        Assert.Equal(id, ServerSettingsLock.Parse(id).Match(id));
        Assert.Null(ServerSettingsLock.Parse("operator").Match(id));
    }

    [Fact]
    public void The_Server_tab_row_reads_the_state_and_is_read_only_while_the_cfg_forces_it()
    {
        JoinAdminState? state = null;
        try
        {
            JoinAdminRows.Reset();
            JoinAdminRows.StateProbe = () => state;
            JoinAdminRows.AdminProbe = () => true;
            var spec = JoinAdminRows.VersionMatching();
            Assert.Equal(JoinAdminOps.VersionMatching, spec.Key);
            Assert.Equal("Version matching", spec.Label);
            Assert.True(spec.ChoiceRadio);
            Assert.Equal(new[] { "range", "minor", "exact" }, spec.Choices);
            Assert.Equal(new[] { VersionMatchingText.LabelRange, VersionMatchingText.LabelMinor, VersionMatchingText.LabelExact }, spec.ChoiceLabels);
            Assert.Contains(JoinAdminRows.AlsoInPanel.Trim(), spec.Help);
            Assert.Null(spec.SafeRead());
            // Before a state arrives the row waits (true); a server older than C3 has no row.
            Assert.True(JoinAdminRows.Supported(JoinAdminOps.VersionMatching));
            state = new JoinAdminState { PolicySupported = true };
            Assert.False(JoinAdminRows.Supported(JoinAdminOps.VersionMatching));
            Assert.Equal("", JoinAdminRows.ServerModRulesLine());

            state = Server();
            Assert.True(JoinAdminRows.Supported(JoinAdminOps.VersionMatching));
            Assert.Equal("range", spec.SafeRead());
            Assert.Null(spec.ReadOnlyNow());
            Assert.Equal("acme.maps: Same minor as 2.0.0; gone.mod: Exact", JoinAdminRows.ServerModRulesLine());
            state = Server(forced: true);
            Assert.Equal("exact", spec.SafeRead());
            Assert.Equal("Set in the server's config file ([ClientPolicy] VersionMatching = exact).", spec.ReadOnlyNow());
            state.Lock.Add("mods-players-need");
            Assert.Equal("mods-players-need", spec.LockedByNow());
            Assert.Equal(("exact", "exact"), JoinAdminRows.ReadPlace(JoinAdminOps.VersionMatching)!.Value);
        }
        finally { JoinAdminRows.Reset(); }
    }
}

/// <summary>
/// C3, the server half: the <c>version-matching</c> and <c>server-mod-rule</c> requests through the admin channel's path
/// (rate limit, handshake, authorization, the lock list, audit, the store, the re-signed catalog), the cfg override, the
/// state, and the judge's <see cref="VersionMatching.SettingsSource"/>. In the lock list's collection: the source, the
/// lock and the cfg are process-wide.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class VersionMatchingServerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private readonly List<string> _policyChanged = new();
    private string _lockText = "";
    private string _cfg = "";
    private double _now = 1000;

    public VersionMatchingServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-matching-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo("acme.chatskin", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("acme.maps", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, true),
            new LoadedModInfo("progenitor", "2026.10.9", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false),
        };
        ModManifest.TestIsServer = true;
        Handshake.ResetTables();
        Plugin.Logger = new TestLogSource();
        JoinAdminServer.Reset();
        JoinAdminServer.WindowGet = () => 60;
        JoinAdminServer.WindowSet = _ => true;
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        JoinAdminServer.PolicyChanged = _policyChanged.Add;
        RefusedJoins.Clock = () => T0;
        ServerSettingsLock.Reset();
        ServerSettingsLock.Source = () => _lockText;
        VersionMatching.SettingsSource = null;
        JoinAdminServer.Install();
        VersionMatchCfg.Source = () => _cfg;
    }

    public void Dispose()
    {
        JoinAdminServer.Uninstall();
        ServerSettingsLock.Reset();
        Handshake.ResetTables();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        VersionMatching.SettingsSource = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private JoinAdminResponse Apply(JoinAdminRequest r, RemoteInstallActor? actor = null, bool handshakeOk = true)
    {
        _now += 7;
        return JoinAdminServer.Apply(r, handshakeOk, actor ?? Admin);
    }

    private static JoinAdminRequest Mode(string value) => new() { Op = JoinAdminOps.VersionMatching, Value = value };

    private static JoinAdminRequest Rule(string id, string rule, string name = "") => new() { Op = JoinAdminOps.ServerModRule, Id = id, Rule = rule, Name = name };

    [Fact]
    public void An_admin_sets_the_mode_it_is_stored_audited_and_nobody_is_rejudged()
    {
        var r = Apply(Mode("exact"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(VersionMatchText.NextJoinNote, r.Note);
        Assert.Equal("exact", r.State!.VersionMatching);
        Assert.True(r.State.MatchingSupported);
        Assert.False(r.State.VersionMatchingForced);
        Assert.Contains("\"versionMatching\": \"exact\"", File.ReadAllText(JoinAdminServer.FilePath));
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/version-matching") && l.Contains("accepted") && l.Contains("exact"));
        // A stored change takes the re-sign path every stored change takes (JoinAdminServer.Apply: decision.Changed); the
        // signed catalog's stamp moves once its canonical form carries the per-mod match.
        Assert.Contains("if (decision.Ok && decision.Changed)", SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinAdminServer.cs"), StringComparison.Ordinal);
        // Applies from the next join: the mid-session re-check is not run.
        Assert.Empty(_policyChanged);
        Assert.Equal(VersionMatchingMode.Exact, VersionMatching.Current().Mode);

        Assert.Equal("Unchanged.", Apply(Mode("exact")).Note);
        var bad = Apply(Mode("strict"));
        Assert.False(bad.Ok);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/version-matching") && l.Contains("Choose range, minor or exact."));
    }

    [Fact]
    public void Only_an_admin_with_a_finished_handshake_changes_it()
    {
        var player = Apply(Mode("exact"), Player);
        Assert.False(player.Ok);
        Assert.Equal(JoinAdminPolicy.NotAdminError, player.Error);
        Assert.Null(player.State);
        Assert.Equal(JoinAdminPolicy.HandshakeError, Apply(Rule("acme.chatskin", "any"), handshakeOk: false).Error);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/version-matching") && l.Contains(JoinAdminPolicy.NotAdminError));
        Assert.Equal(VersionMatchingMode.Range, VersionMatching.Current().Mode);
        Assert.False(File.Exists(JoinAdminServer.FilePath));
    }

    [Fact]
    public void Per_mod_rules_are_for_the_mods_the_server_runs_listed_in_the_state_and_read_by_the_judge()
    {
        var state = JoinAdminServer.BuildState();
        // The loaded mods without Progenitor's own, by name.
        Assert.Equal(new[] { "acme.chatskin", "acme.maps" }, state.ServerMods.Select(m => m.Id));
        Assert.Equal(new[] { "1.2.0", "2.0.0" }, state.ServerMods.Select(m => m.Version));
        Assert.All(state.ServerMods, m => Assert.Equal("", m.Rule));
        Assert.Equal("range", state.VersionMatching);

        var set = Apply(Rule("acme.chatskin", "any", "ChatSkin"));
        Assert.True(set.Ok, set.Error);
        Assert.Equal("any", set.State!.ServerMods.Single(m => m.Id == "acme.chatskin").Rule);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/server-mod-rule/acme.chatskin") && l.Contains("accepted"));
        Assert.Contains("\"serverModRules\"", File.ReadAllText(JoinAdminServer.FilePath));
        Assert.True(VersionMatching.Current().TryRule("acme.chatskin", out var rule));
        Assert.Equal(ClientModRule.Any, rule);
        Assert.Empty(_policyChanged);

        var refused = Apply(Rule("client.only.mod", "exact"));
        Assert.False(refused.Ok);
        Assert.Equal(VersionMatchText.NotRunError, refused.Error);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/server-mod-rule/client.only.mod") && l.Contains(VersionMatchText.NotRunError));
        Assert.Equal(VersionMatchText.ProgenitorError, Apply(Rule("progenitor", "exact")).Error);

        // The mod leaves the server: its rule is still listed (not running) so it can be cleared.
        ModManifest.TestLoadedMods = new[] { new LoadedModInfo("acme.maps", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, true) };
        var after = JoinAdminServer.BuildState();
        var stale = after.ServerMods.Single(m => m.Id == "acme.chatskin");
        Assert.False(stale.Runs);
        Assert.Equal("any", stale.Rule);
        Assert.Equal("ChatSkin", stale.Name);
        Assert.Equal(VersionMatchText.NotRunError, Apply(Rule("acme.chatskin", "exact")).Error);
        Assert.True(Apply(Rule("acme.chatskin", "default")).Ok);
        Assert.False(VersionMatching.Current().TryRule("acme.chatskin", out _));
    }

    [Fact]
    public void The_cfg_override_wins_makes_the_control_read_only_and_keeps_the_per_mod_rules()
    {
        Assert.True(Apply(Rule("acme.chatskin", "any")).Ok);
        Assert.True(Apply(Mode("minor")).Ok);
        _cfg = "exact";
        var s = JoinAdminServer.BuildState();
        Assert.Equal("exact", s.VersionMatching);
        Assert.True(s.VersionMatchingForced);
        Assert.Equal(VersionMatchingMode.Exact, VersionMatching.Current().Mode);
        Assert.True(VersionMatching.Current().TryRule("acme.chatskin", out _));

        var refused = Apply(Mode("range"));
        Assert.False(refused.Ok);
        Assert.Equal(VersionMatchCfg.ForcedNote(VersionMatchingMode.Exact), refused.Error);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/version-matching") && l.Contains("[ClientPolicy] VersionMatching = exact"));
        // Per-mod rules are still the admins'.
        Assert.True(Apply(Rule("acme.maps", "exact")).Ok);

        // Cleared in the cfg: the admins' stored choice is back.
        _cfg = "";
        Assert.Equal(VersionMatchingMode.Minor, VersionMatching.Current().Mode);
        Assert.False(JoinAdminServer.BuildState().VersionMatchingForced);
        _cfg = "nonsense";
        Assert.Equal(VersionMatchingMode.Minor, VersionMatching.Current().Mode);
    }

    [Fact]
    public void The_lock_list_refuses_both_edits_with_the_group_word_or_the_setting_id()
    {
        _lockText = "mods-players-need";
        var mode = Apply(Mode("exact"));
        Assert.False(mode.Ok);
        Assert.Equal("Locked in the server's config file (ServerSettings LockedInGame: mods-players-need).", mode.Error);
        Assert.NotNull(mode.State);
        var rule = Apply(Rule("acme.chatskin", "any"));
        Assert.False(rule.Ok);
        Assert.StartsWith("Locked in the server's config file", rule.Error);
        _lockText = "progenitor/JoinAdmin/version-matching";
        Assert.False(Apply(Rule("acme.chatskin", "any")).Ok);
        Assert.Contains("progenitor/JoinAdmin/version-matching", Apply(Mode("exact")).State!.Lock);
        _lockText = "";
        Assert.True(Apply(Mode("exact")).Ok);
    }

    [Fact]
    public void The_judges_source_is_set_cached_rebuilt_on_change_and_cleared_on_uninstall()
    {
        Assert.NotNull(VersionMatching.SettingsSource);
        var first = VersionMatching.Current();
        Assert.Same(first, VersionMatching.Current());
        Assert.Equal(VersionMatchingMode.Range, first.Mode);
        Assert.True(Apply(Mode("minor")).Ok);
        var second = VersionMatching.Current();
        Assert.NotSame(first, second);
        Assert.Same(second, VersionMatching.Current());
        Assert.Equal(VersionMatchingMode.Minor, second.Mode);
        _cfg = "exact";
        Assert.Equal(VersionMatchingMode.Exact, VersionMatching.Current().Mode);

        // A source someone else set is left alone; ours is cleared.
        Func<VersionMatchSettings> other = () => VersionMatchSettings.Default;
        VersionMatching.SettingsSource = other;
        JoinAdminServer.Uninstall();
        Assert.Same(other, VersionMatching.SettingsSource);
        JoinAdminServer.Install();
        JoinAdminServer.Uninstall();
        Assert.Null(VersionMatching.SettingsSource);
        Assert.Equal(VersionMatchingMode.Range, VersionMatching.Current().Mode);
    }

    [Fact]
    public void A_hand_edited_store_loads_its_mode_and_rules_and_notes_the_skipped_rows()
    {
        File.WriteAllText(JoinAdminServer.FilePath,
            "{\"schema\":2,\"versionMatching\":\"exact\",\"serverModRules\":[{\"id\":\"acme.chatskin\",\"rule\":\"any\"},{\"id\":\"acme.maps\",\"rule\":\"often\"}]}");
        JoinAdminServer.Reset();
        Assert.Equal(VersionMatchingMode.Exact, VersionMatching.Current().Mode);
        Assert.True(VersionMatching.Current().TryRule("acme.chatskin", out _));
        Assert.Contains("serverModRules acme.maps: unknown rule \"often\"", JoinAdminServer.LoadNote);
    }
}
