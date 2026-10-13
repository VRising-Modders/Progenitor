using System;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The 2026-10 id rename (docs/hot-reload.md, "Renamed ids"): ids and files written by an earlier build, under a
/// one-segment vendor prefix, are found again under their current names. "vendor" below stands for that retired prefix.
/// </summary>
public sealed class LegacyIdsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-legacyids-" + Guid.NewGuid().ToString("N"));

    public LegacyIdsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("vendor.progenitor", "progenitor")]
    [InlineData("vendor.progenitor.sample", "progenitor.sample")]
    [InlineData("Vendor.Progenitor.Dev", "Progenitor.Dev")]
    [InlineData("vendor-x.progenitor.sample.alpha", "progenitor.sample.alpha")]
    [InlineData("progenitor", "progenitor")]
    [InlineData("progenitor.sample", "progenitor.sample")]
    [InlineData("progenitor.progenitor", "progenitor.progenitor")]
    [InlineData("vendor.progenitorx", "vendor.progenitorx")]
    [InlineData("a.b.progenitor", "a.b.progenitor")]
    [InlineData("example.mod", "example.mod")]
    [InlineData(".progenitor", ".progenitor")]
    [InlineData("", "")]
    public void An_old_id_reads_as_its_current_id_and_nothing_else_changes(string id, string expected)
    {
        Assert.Equal(expected, LegacyIds.Normalize(id));
        Assert.Equal(!string.Equals(id, expected, StringComparison.Ordinal), LegacyIds.IsLegacy(id));
    }

    [Fact]
    public void A_setting_id_keeps_its_section_and_key()
    {
        Assert.Equal("progenitor/JoinAdmin/allowed-steam-ids", LegacyIds.NormalizeRowId("vendor.progenitor/JoinAdmin/allowed-steam-ids"));
        Assert.Equal("progenitor.sample/Menu/ServerNotice", LegacyIds.NormalizeRowId("vendor.progenitor.sample/Menu/ServerNotice"));
        Assert.Equal("example.mod/General/Count", LegacyIds.NormalizeRowId("example.mod/General/Count"));
        Assert.Equal("operator", LegacyIds.NormalizeRowId("operator"));
    }

    [Theory]
    [InlineData("vendor.progenitor.cfg", ".cfg", "progenitor.cfg")]
    [InlineData("vendor.progenitor.ui.cfg", ".cfg", "progenitor.ui.cfg")]
    [InlineData("vendor.progenitor.sample.json", ".json", "progenitor.sample.json")]
    [InlineData("progenitor.cfg", ".cfg", null)]
    [InlineData("vendor.other.cfg", ".cfg", null)]
    [InlineData("vendor.progenitor.cfg.bak", ".cfg", null)]
    [InlineData("BepInEx.cfg", ".cfg", null)]
    public void A_legacy_file_name_maps_to_its_current_name(string name, string extension, string? expected) =>
        Assert.Equal(expected, LegacyIds.CurrentFileName(name, extension));

    [Fact]
    public void Migration_moves_the_values_keeps_a_backup_and_never_overwrites_a_current_file()
    {
        File.WriteAllText(Path.Combine(_dir, "vendor.progenitor.cfg"), "[A]\nKey = old\n");
        File.WriteAllText(Path.Combine(_dir, "vendor.progenitor.ui.cfg"), "[Ui]\nX = 1\n");
        File.WriteAllText(Path.Combine(_dir, "vendor.progenitor.sample.cfg"), "[S]\nY = legacy\n");
        File.WriteAllText(Path.Combine(_dir, "progenitor.sample.cfg"), "[S]\nY = current\n");
        File.WriteAllText(Path.Combine(_dir, "BepInEx.cfg"), "[Logging]\n");

        var lines = LegacyIds.MigrateFolder(_dir, ".cfg");

        Assert.Equal("[A]\nKey = old\n", File.ReadAllText(Path.Combine(_dir, "progenitor.cfg")));
        Assert.Equal("[A]\nKey = old\n", File.ReadAllText(Path.Combine(_dir, "vendor.progenitor.cfg.bak")));
        Assert.False(File.Exists(Path.Combine(_dir, "vendor.progenitor.cfg")));
        Assert.Equal("[Ui]\nX = 1\n", File.ReadAllText(Path.Combine(_dir, "progenitor.ui.cfg")));

        // Both exist: the current file wins and the older one is left untouched.
        Assert.Equal("[S]\nY = current\n", File.ReadAllText(Path.Combine(_dir, "progenitor.sample.cfg")));
        Assert.True(File.Exists(Path.Combine(_dir, "vendor.progenitor.sample.cfg")));
        Assert.Contains(lines, l => l.Contains("kept progenitor.sample.cfg", StringComparison.Ordinal));

        Assert.True(File.Exists(Path.Combine(_dir, "BepInEx.cfg")));
        Assert.Equal(3, lines.Count);

        // Idempotent: a second start finds nothing left to move.
        File.Delete(Path.Combine(_dir, "vendor.progenitor.sample.cfg"));
        Assert.Empty(LegacyIds.MigrateFolder(_dir, ".cfg"));
    }

    [Fact]
    public void A_second_legacy_copy_gets_its_own_backup_name()
    {
        File.WriteAllText(Path.Combine(_dir, "vendor.progenitor.dev.cfg.bak"), "earlier backup");
        File.WriteAllText(Path.Combine(_dir, "vendor.progenitor.dev.cfg"), "values");

        LegacyIds.MigrateFolder(_dir, ".cfg");

        Assert.Equal("values", File.ReadAllText(Path.Combine(_dir, "progenitor.dev.cfg")));
        Assert.Equal("earlier backup", File.ReadAllText(Path.Combine(_dir, "vendor.progenitor.dev.cfg.bak")));
        Assert.Single(Directory.GetFiles(_dir, "vendor.progenitor.dev.cfg.bak-*"));
    }

    [Fact]
    public void A_missing_folder_is_not_an_error() => Assert.Empty(LegacyIds.MigrateFolder(Path.Combine(_dir, "absent"), ".cfg"));

    [Fact]
    public void The_lock_list_reads_an_old_setting_id_as_its_current_one()
    {
        var set = ServerSettingsLock.Parse("vendor.progenitor/ServerMessage/Message");
        Assert.Equal(new[] { "progenitor/ServerMessage/Message" }, set.Entries);
        Assert.Equal("progenitor/ServerMessage/Message", set.Match("progenitor/ServerMessage/Message"));
    }

    [Fact]
    public void The_join_admin_store_reads_old_mod_ids_as_current_ones()
    {
        const string json = "{\"schema\":2,\"recommended\":[{\"id\":\"vendor.progenitor.sample\",\"package\":\"\",\"version\":\"1.0.0\",\"reason\":\"r\"}],"
            + "\"requiredClient\":[{\"id\":\"vendor.progenitor.samplecompanion\",\"version\":\"1.0.0\",\"rule\":\"atleast\",\"reason\":\"r\"}],"
            + "\"clientPolicy\":{\"mode\":\"approved\",\"approved\":[{\"id\":\"vendor.progenitor.dyeall\"},{\"id\":\"example.mod\"}],\"unverified\":\"allow\"}}";
        Assert.True(JoinAdminConfig.TryParse(json, out var config, out var error), error);
        Assert.Equal("progenitor.sample", Assert.Single(config.Recommended).Id);
        Assert.Equal("progenitor.samplecompanion", Assert.Single(config.Policy.Required).Id);
        Assert.Equal(new[] { "progenitor.dyeall", "example.mod" }, config.Policy.Approved.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void An_older_peer_still_names_progenitor_itself()
    {
        Assert.True(ModManifestJson.IsFrameworkGuid("vendor.progenitor"));
        Assert.True(ModManifestJson.IsFrameworkGuid("progenitor"));
        Assert.False(ModManifestJson.IsFrameworkGuid("progenitor.sample"));
        Assert.True(ClientModJudge.AlwaysApproved("vendor.progenitor.dev"));
        Assert.True(ModEnableState.IsRequiredPart("vendor.progenitor.preloader"));
    }

    [Fact]
    public void A_refused_client_with_old_ids_is_told_its_progenitor_is_older()
    {
        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved };
        var report = new ClientModReport();
        report.Entries.Add(new ClientModEntry("vendor.progenitor", "Progenitor", "1.0.0", ClientModSources.Chainloader));
        report.Entries.Add(new ClientModEntry("vendor.progenitor.sample", "Sample", "1.0.0", ClientModSources.Chainloader));

        var verdict = ClientModJudge.Judge(policy, report, new ClientModServerFacts());

        Assert.Equal(ClientJudgmentOutcome.Refuse, verdict.Outcome);
        Assert.StartsWith(ClientModJudge.OlderProgenitorReason + " ", verdict.Reason, StringComparison.Ordinal);
        // Progenitor itself is not listed as a mod that is not allowed; the old sample is.
        Assert.Equal(new[] { "vendor.progenitor.sample" }, verdict.NotAllowed.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void A_current_client_gets_no_rename_sentence()
    {
        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved };
        var report = new ClientModReport();
        report.Entries.Add(new ClientModEntry("cheaty.mod", "Cheaty", "1.0.0", ClientModSources.Chainloader));
        var verdict = ClientModJudge.Judge(policy, report, new ClientModServerFacts());
        Assert.Equal(ClientJudgmentOutcome.Refuse, verdict.Outcome);
        Assert.DoesNotContain(ClientModJudge.OlderProgenitorReason, verdict.Reason, StringComparison.Ordinal);
    }
}
