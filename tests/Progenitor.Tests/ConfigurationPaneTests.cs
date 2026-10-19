using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Configuration tab: settings-kit binds, source defaults, folder Apply/Clear, and the
/// framework settings action. Guard removals named in comments must fail these tests.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigurationPaneTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _file;
    private readonly object _owner = new();

    public ConfigurationPaneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-tabs2-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        UiApi.ResetForTests();
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        ConfigHelper.UninstallAll();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.ReparseLookup = null;
        ServerInstall.RunningServerProbe = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    [Fact]
    public void Thunderstore_defaults_on_and_Nexus_defaults_off_with_the_qualification_note()
    {
        var bindings = ConfigurationPane.Bind(_file);
        Assert.True(bindings.UseThunderstore.Value);
        Assert.False(bindings.UseNexus.Value);
        Assert.Equal("", bindings.NexusApiKey.Value);
        Assert.Contains("API key", ConfigurationPane.NexusNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not yet supported", ConfigurationPane.NexusNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModsConfigKeys.UseThunderstoreScope, ModsConfigKeys.Scope(ModsConfigKeys.UseThunderstore));
        Assert.Equal(ModsConfigKeys.NexusApiKeyScope, ModsConfigKeys.Scope(ModsConfigKeys.NexusApiKey));
        Assert.Equal(ModsConfigKeys.DedicatedServerPathScope, ModsConfigKeys.Scope(ModsConfigKeys.DedicatedServerPath));
        // Revert: defaulting Nexus on, or dropping the API-key/not-qualified note, fails this.
    }

    [Fact]
    public void Show_nsfw_content_defaults_off_is_personal_and_is_a_sources_row()
    {
        var bindings = ConfigurationPane.Bind(_file);
        Assert.False(bindings.ShowNsfwContent.Value);
        Assert.Equal(ModsConfigKeys.Section, bindings.ShowNsfwContent.Definition.Section);
        Assert.Equal(ModsConfigKeys.ShowNsfwContent, bindings.ShowNsfwContent.Definition.Key);
        Assert.Equal(ModsConfigKeys.ShowNsfwContentScope, ModsConfigKeys.Scope(ModsConfigKeys.ShowNsfwContent));
        Assert.Contains("flag", ConfigurationPane.ShowNsfwHelp, StringComparison.Ordinal);
        Assert.Contains("never caught", ConfigurationPane.ShowNsfwHelp, StringComparison.Ordinal);
        var pane = new ConfigurationPane(bindings, new LocalServerFolder(), _owner);
        var rows = pane.BuildRows();
        Assert.Contains(rows, r => r.Key == ModsConfigKeys.ShowNsfwContent && r.Kind == SettingsEditorKind.Bool);
        Assert.Equal(ConfigurationPane.ShowNsfwLabel, LabelOf(rows, ModsConfigKeys.ShowNsfwContent));
        // The Install tab reads the entry when it filters; nothing here subscribes to the shared file.
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        Assert.DoesNotContain("ShowNsfwContent.SettingChanged", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Nexus_key_display_never_echoes_the_secret()
    {
        const string secret = "super-secret-nexus-key";
        Assert.Equal("not set", ConfigurationPane.NexusKeyDisplay(""));
        Assert.Equal("configured", ConfigurationPane.NexusKeyDisplay(secret));
        Assert.DoesNotContain(secret, ConfigurationPane.NexusKeyDisplay(secret));
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        SourcePin.AssertNoStatementContaining(src, "LogInfo(bindings.NexusApiKey");
        SourcePin.AssertNoStatementContaining(src, "LogWarning(bindings.NexusApiKey");
        // Revert: returning the raw key from NexusKeyDisplay fails DoesNotContain.
    }

    [Fact]
    public void Settings_kit_rows_cover_sources_and_folder_actions_and_the_purpose_links_replace_Progenitor_settings()
    {
        var bindings = ConfigurationPane.Bind(_file);
        var pane = new ConfigurationPane(bindings, new LocalServerFolder(), _owner);
        bool mods = false, server = false;
        pane.SetLinks(() => mods = true, () => server = true);
        var rows = pane.BuildRows();
        Assert.Contains(rows, r => r.Key == ModsConfigKeys.UseThunderstore && r.Kind == SettingsEditorKind.Bool);
        Assert.DoesNotContain(rows, r => r.Key == ModsConfigKeys.UseNexus);
        Assert.DoesNotContain(rows, r => r.Key == ModsConfigKeys.NexusApiKey);
        Assert.Contains(rows, r => r.Key == ModsConfigKeys.DedicatedServerPath && r.Kind == SettingsEditorKind.String);
        Assert.Contains(rows, r => r.Key == ConfigurationPane.ApplyFolderAction);
        Assert.Contains(rows, r => r.Key == ConfigurationPane.ClearFolderAction);
        // Settings organization stage 3: the raw "Progenitor settings" action is gone; the link row opens Progenitor's page.
        Assert.DoesNotContain(rows, r => r.Key == "OpenProgenitorSettings");
        Assert.Equal("Use Thunderstore", LabelOf(rows, ModsConfigKeys.UseThunderstore));
        Assert.Equal("Apply", LabelOf(rows, ConfigurationPane.ApplyFolderAction));
        // Three homes (decided 2026-10-06): the four groups of Preferences, in order.
        Assert.Equal(new[] { "Mods tab", "Install Mods tab", "Sources and downloads", "Updates", "Join Game", "Title screen", "Local server" }, ConfigurationPane.ConfigGroups);
        Assert.Equal(ConfigurationPane.ConfigGroups, ConfigurationPane.DetailSectionHeadings);
        Assert.False(ModsTabState.IsModCatalogueSidebar(ModsSidebarKind.ConfigGroups));

        Assert.All(rows, r => Assert.Equal("left", r.Alignment));
        Assert.True(pane.OpenMods().Completed);
        Assert.True(pane.OpenServer().Completed);
        Assert.True(mods && server);
        pane.Dispose();
        // Revert: putting the Progenitor settings action back, or using a mod sidebar here, fails this.
        // Revert: putting Nexus back as a toggle/key field, or restoring [Mods] keys as captions, fails this.
    }

    [Fact]
    public void The_configured_folder_is_not_a_suggestion_and_the_others_fold_away()
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-fold-" + Guid.NewGuid().ToString("N"));
        LocalServerFolder.WriteAccessOverride = _ => true;
        try
        {
            string MakeServer(string name)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(dir, LocalServerFolder.BepInExFolderName));
                File.WriteAllText(Path.Combine(dir, LocalServerFolder.ServerExecutableName), "exe");
                return Path.GetFullPath(dir);
            }

            string configured = MakeServer("server");
            string other = MakeServer("headless");
            var sources = new FakeDiscoverySources();
            sources.Running.Add(configured);
            sources.Running.Add(other);
            var bindings = ConfigurationPane.Bind(_file);
            var folder = new LocalServerFolder();
            var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources));
            Assert.True(pane.ApplyDraft(configured).Completed);

            var panel = new RecordingConfigPanel();
            pane.Build(panel);
            // The configured folder is never offered again; its chips go on its own status line.
            Assert.DoesNotContain(pane.DiscoveredFolders, p => string.Equals(p, configured, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(other, pane.DiscoveredFolders.Single(), StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(pane.ConfiguredFound);
            Assert.Contains(panel.Labels, l => l.Text.StartsWith(ConfigurationPane.ValidatedPrefix, StringComparison.Ordinal)
                && l.Text.Contains(" · " + SteamLibraryScan.SourceRunning, StringComparison.Ordinal));
            Assert.StartsWith(ConfigurationPane.ValidatedPrefix, pane.StatusLine); // the plain status is unchanged
            Assert.Null(pane.SingleSuggestion);
            // Collapsed: the header's Show, no Use this folder.
            Assert.False(pane.OtherServersOpen);
            Assert.Contains(panel.Buttons, b => b.Text == ConfigurationPane.ShowCaption);
            Assert.DoesNotContain(panel.Buttons, b => b.Text == ConfigurationPane.UseThisFolder);
            Assert.Equal("Other servers found (1)", ConfigurationPane.OtherServersHeader(1));

            // Expanded.
            pane.OtherServersOpen = true;
            var open = new RecordingConfigPanel();
            pane.Build(open);
            Assert.Contains(open.Buttons, b => b.Text == ConfigurationPane.HideCaption);
            Assert.Single(open.Buttons, b => b.Text == ConfigurationPane.UseThisFolder);
            pane.Dispose();
        }
        finally
        {
            LocalServerFolder.WriteAccessOverride = null;
            ConfigurationPane.ResetOtherServersOpen();
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Long_paths_are_elided_in_the_middle()
    {
        string path = @"C:\GameModding\_testserver\headless\server\some\very\long\folder\name";
        string short_ = ConfigurationPane.MiddleEllipsis(path, 30);
        Assert.Equal(30, short_.Length);
        Assert.StartsWith(@"C:\GameModding", short_);
        Assert.EndsWith(@"folder\name", short_);
        Assert.Contains("…", short_);
        Assert.Equal(@"C:\short", ConfigurationPane.MiddleEllipsis(@"C:\short", 30));
    }

    [Fact]
    public void Exactly_one_found_server_is_offered_prominently_and_never_applied()
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-single-" + Guid.NewGuid().ToString("N"));
        try
        {
            string server = Path.Combine(root, "Server");
            Directory.CreateDirectory(Path.Combine(server, LocalServerFolder.BepInExFolderName));
            File.WriteAllText(Path.Combine(server, LocalServerFolder.ServerExecutableName), "exe");
            var sources = new FakeDiscoverySources();
            sources.Running.Add(server);
            var bindings = ConfigurationPane.Bind(_file);
            var folder = new LocalServerFolder();
            var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources));
            var panel = new RecordingConfigPanel();
            pane.Build(panel);
            string canon = Path.GetFullPath(server);
            Assert.NotNull(pane.SingleSuggestion);
            Assert.Contains(panel.Labels, l => string.Equals(l.Text, ConfigurationPane.FoundSingleLine(canon), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(panel.Labels, l => l.Text.Contains("Stop server act on that folder", StringComparison.Ordinal));
            // The source and chips are the row's hover description now; the row's control is the key cap.
            Assert.Contains(SteamLibraryScan.SourceRunning, pane.DiscoveredSuggestions.Single().Detail);
            Assert.Single(panel.Buttons, b => b.Text == ConfigurationPane.UseThisFolder);
            Assert.False(folder.HasValidatedServer);
            Assert.Equal("", bindings.DedicatedServerPath.Value);

            // Two candidates: no single prompt, each still offered.
            string second = Path.Combine(root, "Second");
            Directory.CreateDirectory(Path.Combine(second, LocalServerFolder.BepInExFolderName));
            File.WriteAllText(Path.Combine(second, LocalServerFolder.ServerExecutableName), "exe");
            sources.Uninstall.Add(second);
            var both = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources));
            var panel2 = new RecordingConfigPanel();
            both.Build(panel2);
            Assert.Null(both.SingleSuggestion);
            Assert.Equal(2, both.DiscoveredFolders.Count);
            Assert.Equal(2, panel2.Buttons.Count(b => b.Text == ConfigurationPane.UseThisFolder));
            pane.Dispose();
            both.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Configuration_is_three_left_sections_and_Nexus_is_a_dimmed_line()
    {
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var scan = new SteamLibraryScan(new MapSteamLibraryFileSystem());
        var pane = new ConfigurationPane(bindings, folder, _owner, scan);
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Equal("left", ConfigurationPane.LayoutAlignment);
        Assert.NotEqual(ConfigurationLayout.RetiredCentredDump, ConfigurationPane.LayoutAlignment);
        Assert.Equal("left", panel.ContentAlignment);
        // Three homes: the tab's title is the one game section header; Mods tab, Install Mods tab and Sources
        // and downloads are drawn by the row kit, and Local server's heading is a subheading row. No Progenitor group.
        Assert.Empty(HeadingsIn(panel));
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.PreferencesTitle);
        Assert.Single(panel.SectionHeadings);
        // Mods tab holds only Edit other mods' settings, which this test does not bind, so the group is not drawn here.
        Assert.Equal(new[] { ConfigurationPane.InstallTabGroup, ConfigurationPane.SourcesGroup, ConfigurationPane.UpdatesGroup },
            pane.SettingsView!.Sections.Select(s => s.Title).ToArray());
        Assert.All(panel.Labels, l => Assert.Equal("left", l.Align));
        Assert.All(panel.Toggles, t => Assert.Equal("left", t.Align));
        Assert.All(panel.Buttons, b => Assert.Equal("left", b.Align));
        Assert.True(panel.InlineBegan);
        Assert.Contains(panel.Toggles, t => t.Text == "Use Thunderstore");
        // The 2026-10-06 trim: no Nexus placeholder line under Sources any more, and Nexus is never a switch or a key field.
        Assert.DoesNotContain(pane.SettingsView!.Sections.Single(s => s.Title == ConfigurationPane.SourcesGroup).Items,
            i => (i.Label ?? "").IndexOf("Nexus", StringComparison.OrdinalIgnoreCase) >= 0);
        // 2026-09-12: the private-game note sits under the Local dedicated server heading.
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.PrivateGameNote);
        Assert.Contains("private games", ConfigurationPane.PrivateGameNote, StringComparison.Ordinal);
        Assert.DoesNotContain(panel.Toggles, t => t.Text.IndexOf("Nexus", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.DoesNotContain(panel.Inputs, i => i.Text.IndexOf("Nexus", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.DoesNotContain(panel.AllText, t => t.IndexOf("[Mods]", StringComparison.Ordinal) >= 0);
        Assert.Contains(panel.Buttons, b => b.Text == "Apply");
        Assert.Contains(panel.Buttons, b => b.Text == "Clear");
        Assert.DoesNotContain(panel.Buttons, b => b.Text == "Progenitor settings");
        Assert.DoesNotContain(panel.Buttons, b => b.Text == ConfigurationPane.UseThisFolder);
        Assert.False(folder.HasValidatedServer);
        Assert.Empty(pane.DiscoveredFolders);

        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs"),
            "public void Build");
        SourcePin.AssertUncommented(body, "LocalServerHeading(");
        SourcePin.AssertUncommented(body, "ContentAlignment");
        SourcePin.AssertUncommented(body, "Heading(");
        // Three homes (live 2026-10-06): the folder field takes the row on the workspace; Apply and Clear go under it.
        SourcePin.AssertUncommented(body, "BuildFolderRows(panel)");
        SourcePin.AssertUncommented(body, "BuildFolderInline(panel)");
        string make = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"),
            "private static GameObject? MakeLabel(");
        SourcePin.AssertUncommented(make, "left ? TextAlignmentOptions.Left : TextAlignmentOptions.Center");
        SourcePin.AssertNoStatementContaining(body, "AddRowForTests");
        SourcePin.AssertNoStatementContaining(body, ConfigurationLayout.RetiredCentredDump);
        pane.Dispose();
        // Revert: dropping ContentAlignment left, Heading, or BeginInline restores the centered dump.
    }

    [Fact]
    public void Discovery_offers_validated_vdf_candidates_and_never_applies_them()
    {
        string steam = Path.Combine(_dir, "Steam");
        string library = Path.Combine(_dir, "LibraryA");
        string valid = MakeServerAt(Path.Combine(library, "steamapps", "common", "VRisingDedicatedServer"));
        string missingExe = Path.Combine(_dir, "LibraryB", "steamapps", "common", "VRisingDedicatedServer");
        Directory.CreateDirectory(missingExe);
        Directory.CreateDirectory(Path.Combine(missingExe, LocalServerFolder.BepInExFolderName));

        string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\""
            + steam.Replace("\\", "\\\\")
            + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\""
            + library.Replace("\\", "\\\\")
            + "\"\n\t}\n\t\"2\"\n\t{\n\t\t\"path\"\t\t\""
            + Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(missingExe)))!.Replace("\\", "\\\\")
            + "\"\n\t}\n}\n";
        var fs = new MapSteamLibraryFileSystem();
        fs.Roots.Add(steam);
        fs.Files[Path.Combine(steam, SteamLibraryScan.LibrariesVdfRelative)] = vdf;
        var scan = new SteamLibraryScan(fs);
        var hits = scan.Suggest();
        Assert.Single(hits);
        Assert.Equal(Path.GetFullPath(valid), hits[0], StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(hits, h => h.IndexOf("LibraryB", StringComparison.OrdinalIgnoreCase) >= 0);

        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, scan);
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.False(folder.HasValidatedServer);
        Assert.Equal("", bindings.DedicatedServerPath.Value);
        Assert.Single(pane.DiscoveredFolders);
        Assert.Equal(hits[0], pane.DiscoveredFolders.Single(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(panel.Buttons, b => b.Text == ConfigurationPane.UseThisFolder);
        pane.Dispose();
        Assert.False(folder.HasValidatedServer);
        // Revert: auto-applying a discovered folder, or offering a folder without VRisingServer.exe, fails this.
    }

    private static string LabelOf(IReadOnlyList<SettingsRow> rows, string key)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key) return rows[i].Label;
        }

        return "";
    }

    [Fact]
    public void Apply_stores_a_valid_folder_and_refuses_without_replacing_the_previous()
    {
        string server = MakeServer("ok");
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner);
        var first = pane.ApplyDraft(server);
        Assert.True(first.Completed, first.Message);
        Assert.True(folder.HasValidatedServer);
        string identity = folder.Identity!;
        Assert.Equal(identity, bindings.DedicatedServerPath.Value);

        var refused = pane.ApplyDraft(Path.Combine(_dir, "missing-server"));
        Assert.Equal(SettingActionOutcome.Refused, refused.Outcome);
        Assert.True(folder.HasValidatedServer);
        Assert.Equal(identity, folder.Identity);
        Assert.Equal(identity, bindings.DedicatedServerPath.Value);
        pane.Dispose();
        // Revert: storing the refused path, or clearing the previous valid folder, fails this.
    }

    [Fact]
    public void Clear_drops_the_folder_so_Install_has_no_server_targets()
    {
        string server = MakeServer("clr");
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner);
        Assert.True(pane.ApplyDraft(server).Completed);
        Assert.True(pane.ClearFolder().Completed);
        Assert.False(folder.HasValidatedServer);
        Assert.Equal("", bindings.DedicatedServerPath.Value);
        Assert.Single(InstallModsPane.TargetChoices(folder));
        pane.Dispose();
        // Revert: Clear leaving HasValidatedServer true keeps Local server / Both visible.
    }


    /// <summary>
    /// INSTALLUX1 item 2: the Configuration row <c>Default install target</c>, under the Local
    /// dedicated server heading, bound to <c>[Install] DefaultTarget</c>. It is the Install tab's
    /// whole answer to "where does this go?" now that the per-row chooser is gone.
    /// </summary>
    [Fact]
    public void Default_install_target_is_a_four_value_row_bound_to_the_install_section()
    {
        var entry = ConfigHelper.BindDefaultInstallTarget(_file);
        Assert.Equal(DefaultInstallTarget.FollowSideValue, entry.Value);
        Assert.Equal(ConfigHelper.InstallSection, entry.Definition.Section);
        Assert.Equal(ConfigHelper.DefaultInstallTargetKey, entry.Definition.Key);
        Assert.Equal(DefaultInstallTarget.Default, DefaultInstallTargetConfig.Resolve());

        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner);
        Assert.Equal(DefaultInstallTarget.FollowSideValue, pane.DefaultInstallTargetValue);
        Assert.Equal(InstallModsCopy.DefaultTargetLabel, ConfigurationPane.DefaultInstallTargetLabel);
        Assert.Contains(
            ConfigurationPane.LocalDedicatedHeading,
            ConfigurationPane.DetailSectionHeadings);

        // Writing the row writes the config key, and an unknown value normalizes to the default.
        pane.DefaultInstallTargetValue = DefaultInstallTarget.ServerCaption;
        // UI polish: the description follows the selection and the route (no folder here: no server).
        Assert.StartsWith("Mods install to the server only.", pane.DefaultInstallTargetDescription);
        Assert.Contains("Right now: not available", pane.DefaultInstallTargetDescription);
        pane.RouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        Assert.Contains("the server you're connected to", pane.DefaultInstallTargetDescription);
        pane.RouteProbe = null;
        Assert.Equal(DefaultInstallTarget.ServerValue, entry.Value);
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTargetConfig.Resolve());
        // REMOTEINST1: the old caption still reads as the one Server target, and is written back new.
        pane.DefaultInstallTargetValue = DefaultInstallTarget.LegacyLocalServerCaption;
        Assert.Equal(DefaultInstallTarget.ServerValue, entry.Value);
        // A stored legacy value is migrated the first time the setting is resolved.
        entry.Value = DefaultInstallTarget.LegacyLocalServerValue;
        Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTargetConfig.Resolve());
        Assert.Equal(DefaultInstallTarget.ServerValue, entry.Value);
        pane.DefaultInstallTargetValue = "nonsense";
        Assert.Equal(DefaultInstallTarget.FollowSideValue, entry.Value);

        // The four options are the four settings, captioned.
        Assert.Equal(
            new[]
            {
                DefaultInstallTarget.FollowSideCaption,
                DefaultInstallTarget.BothCaption,
                DefaultInstallTarget.ClientCaption,
                DefaultInstallTarget.ServerCaption,
            },
            DefaultInstallTarget.All.Select(DefaultInstallTarget.CaptionOf).ToArray());
        pane.Dispose();
        // Revert: binding the key in another section, or letting an unknown stored value through
        // instead of normalizing it to followSide, fails this.
    }

    // --- CONF1: normalize on Apply and on load, validate on build, always show the outcome ---

    [Fact]
    public void Config_provided_path_is_validated_on_build_and_the_status_names_it()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        string server = MakeServer("cfg-load");
        var bindings = ConfigurationPane.Bind(_file);
        bindings.DedicatedServerPath.Value = "\u200B" + server + "\u200B";
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()), InstallSource());
        Assert.True(folder.HasValidatedServer);
        string canon = folder.Current.CanonicalPath!;
        Assert.Equal(ConfigurationPane.ValidatedStatus(canon), pane.StatusLine);
        Assert.Equal(canon, pane.DraftPath);
        Assert.Equal(-1, pane.DraftPath.IndexOf((char)0x200B));

        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.ValidatedPrefix + canon);
        Assert.DoesNotContain(panel.Labels, l => l.Text == LocalServerFolder.RefusalEmpty);
        Assert.Contains(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        Assert.Equal("not-installed", pane.ServerInstallReadback()["state"]);
        // The folder field (the row kit's text fields, such as the update tag, are inputs too).
        Assert.Equal(canon, panel.Inputs.Single(i => i.Text == ConfigurationPane.ServerFolderLabel).Value);
        pane.Dispose();
        // Revert: skipping _folder.Apply in the constructor shows "No local dedicated-server folder is configured." for a valid configured path.
    }

    [Fact]
    public void Config_provided_path_shows_the_refusal_row_while_that_server_runs()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        string server = MakeServer("cfg-running");
        string exe = Path.Combine(server, LocalServerFolder.ServerExecutableName);
        ServerInstall.RunningServerProbe = _ => exe;
        var bindings = ConfigurationPane.Bind(_file);
        bindings.DedicatedServerPath.Value = server;
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()), InstallSource());
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.StartsWith(ConfigurationPane.ValidatedPrefix, pane.StatusLine);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        // UI polish: one short line; the full sentence (with the path) is the readback and the hover description.
        Assert.Single(panel.Labels, l => l.Text == ConfigurationPane.ServerRunningLine);
        Assert.Contains(exe, (string)pane.ServerInstallReadback()["reason"]!);
        Assert.Equal("refused", pane.ServerInstallReadback()["state"]);
        pane.Dispose();
        // Revert: not validating the configured path on build leaves no serverInstall row at all.
    }

    [Fact]
    public void Config_provided_invalid_path_shows_its_exact_refusal_on_build()
    {
        var bindings = ConfigurationPane.Bind(_file);
        string missing = Path.Combine(_dir, "nowhere");
        bindings.DedicatedServerPath.Value = missing;
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()));
        Assert.False(folder.HasValidatedServer);
        Assert.Equal(ConfigurationPane.RefusedStatus(LocalServerFolder.RefusalNotDirectory, missing), pane.StatusLine);
        Assert.Contains(missing, pane.StatusLine);
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == pane.StatusLine);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        pane.Dispose();
        // Revert: reporting RefusalEmpty for a non-empty configured path hides the real reason.
    }

    [Fact]
    public void Apply_strips_zero_width_marks_whitespace_and_quotes_persists_and_recomposes()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        string server = MakeServer("strip");
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()), InstallSource());
        int rebuilds = 0;
        pane.SetRebuild(() => rebuilds++);
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Equal(ConfigurationPane.FolderNotConfigured, pane.StatusLine);

        // The field hands back the kit's zero-width marker plus a quoted, padded path.
        panel.Inputs.Single(i => i.Text == ConfigurationPane.ServerFolderLabel).Fire("\uFEFF  \"" + server + "\\\"  \u200B");
        Assert.Equal(-1, pane.DraftPath.IndexOf((char)0x200B));
        Assert.DoesNotContain("\"", pane.DraftPath);
        var applied = pane.ApplyDraft();
        Assert.True(applied.Completed, applied.Message);
        string canon = folder.Current.CanonicalPath!;
        Assert.Equal(canon, bindings.DedicatedServerPath.Value);
        Assert.Equal(ConfigurationPane.ValidatedStatus(canon), pane.StatusLine);
        Assert.Equal(pane.StatusLine, applied.Message);
        Assert.Equal(1, rebuilds);
        Assert.Equal(-1, bindings.DedicatedServerPath.Value.IndexOf((char)0x200B));
        Assert.Equal(-1, bindings.DedicatedServerPath.Value.IndexOf((char)0xFEFF));

        panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.ValidatedPrefix + canon);
        Assert.Contains(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        pane.Dispose();
        // Revert: validating the literal field text (U+200B kept) refuses a real folder as "not an existing directory".
        // Revert: dropping RequestRebuild from ApplyDraft leaves the stale status note and no serverInstall rows.
    }

    [Theory]
    [InlineData("missing-dir", false, false, false, LocalServerFolder.RefusalNotDirectory)]
    [InlineData("no-exe", true, false, true, LocalServerFolder.RefusalMissingExe)]
    [InlineData("no-bepinex", true, true, false, LocalServerFolder.RefusalMissingBepInEx)]
    [InlineData("unwritable", true, true, true, LocalServerFolder.RefusalUnwritable)]
    public void Apply_shows_each_refusal_with_the_path_and_keeps_the_previous_folder(
        string name, bool makeDir, bool makeExe, bool makeBepInEx, string expected)
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        string good = MakeServer("good");
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()));
        Assert.True(pane.ApplyDraft(good).Completed);
        string goodCanon = bindings.DedicatedServerPath.Value;

        string bad = Path.Combine(_dir, name);
        if (makeDir) Directory.CreateDirectory(bad);
        if (makeExe) File.WriteAllText(Path.Combine(bad, LocalServerFolder.ServerExecutableName), "exe");
        if (makeBepInEx) Directory.CreateDirectory(Path.Combine(bad, LocalServerFolder.BepInExFolderName));
        if (expected == LocalServerFolder.RefusalUnwritable)
            LocalServerFolder.WriteAccessOverride = dir => !dir.EndsWith(name, StringComparison.OrdinalIgnoreCase);

        int rebuilds = 0;
        pane.SetRebuild(() => rebuilds++);
        var refused = pane.ApplyDraft("  " + bad + "  ");
        Assert.Equal(SettingActionOutcome.Refused, refused.Outcome);
        Assert.Equal(ConfigurationPane.RefusedStatus(expected, bad), pane.StatusLine);
        Assert.Equal(pane.StatusLine, refused.Message);
        Assert.Contains(expected, pane.StatusLine);
        Assert.Contains(bad, pane.StatusLine);
        Assert.Equal(1, rebuilds);
        Assert.True(folder.HasValidatedServer);
        Assert.Equal(goodCanon, bindings.DedicatedServerPath.Value);
        pane.Dispose();
        // Revert: a generic "not configured" line, or persisting the refused path, fails this.
    }

    [Fact]
    public void Clear_empties_the_key_shows_Not_configured_and_recomposes()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        string server = MakeServer("clear2");
        var bindings = ConfigurationPane.Bind(_file);
        var folder = new LocalServerFolder();
        var pane = new ConfigurationPane(bindings, folder, _owner, new SteamLibraryScan(new MapSteamLibraryFileSystem()), InstallSource());
        Assert.True(pane.ApplyDraft(server).Completed);
        int rebuilds = 0;
        pane.SetRebuild(() => rebuilds++);
        Assert.True(pane.ClearFolder().Completed);
        Assert.Equal("", bindings.DedicatedServerPath.Value);
        Assert.Equal("", pane.DraftPath);
        Assert.Equal(ConfigurationPane.FolderNotConfigured, pane.StatusLine);
        Assert.Equal(1, rebuilds);
        var panel = new RecordingConfigPanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.FolderNotConfigured);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        pane.Dispose();
        // Revert: Clear without RequestRebuild leaves the serverInstall toggle on screen for no folder.
    }

    private ServerInstallSource InstallSource()
    {
        string client = Path.Combine(_dir, "client", "BepInEx");
        Directory.CreateDirectory(Path.Combine(client, "plugins"));
        Directory.CreateDirectory(Path.Combine(client, "progenitor-core"));
        File.WriteAllText(Path.Combine(client, "plugins", "Progenitor.Bootstrap.dll"), "bootstrap");
        File.WriteAllText(Path.Combine(client, "progenitor-core", "Progenitor.dll"), "core");
        return ServerInstallSource.FromClientRoot(client, "0.1.0");
    }

    private string MakeServer(string name) => MakeServerAt(Path.Combine(_dir, name));

    private static string MakeServerAt(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(root, LocalServerFolder.BepInExFolderName));
        Directory.CreateDirectory(Path.Combine(root, LocalServerFolder.BepInExFolderName, "core"));
        File.WriteAllText(Path.Combine(root, LocalServerFolder.BepInExFolderName, "core", "BepInEx.Core.dll"), "bepinex");
        return root;
    }

    private static string[] HeadingsIn(RecordingConfigPanel panel)
    {
        var order = new List<string>();
        for (int i = 0; i < panel.Labels.Count; i++)
        {
            string text = panel.Labels[i].Text;
            for (int h = 0; h < ConfigurationPane.DetailSectionHeadings.Length; h++)
            {
                if (string.Equals(text, ConfigurationPane.DetailSectionHeadings[h], StringComparison.Ordinal))
                    order.Add(text);
            }
        }

        return order.ToArray();
    }

    private sealed class RecordingConfigPanel : IUiPanel
    {
        public List<RecordingConfigLabel> Labels { get; } = new();
        public List<RecordingConfigButton> Buttons { get; } = new();
        public List<RecordingConfigToggle> Toggles { get; } = new();
        public List<RecordingConfigInput> Inputs { get; } = new();
        public List<string> SectionHeadings { get; } = new();
        public bool InlineBegan { get; private set; }
        public string ContentAlignment { get; set; } = SettingsAlign.Centre;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "Configuration";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }

        public IEnumerable<string> AllText
        {
            get
            {
                for (int i = 0; i < Labels.Count; i++) yield return Labels[i].Text;
                for (int i = 0; i < Buttons.Count; i++) yield return Buttons[i].Text;
                for (int i = 0; i < Toggles.Count; i++) yield return Toggles[i].Text;
                for (int i = 0; i < Inputs.Count; i++) yield return Inputs[i].Text;
            }
        }

        public IUiButton Button(string text)
        {
            var button = new RecordingConfigButton { Text = text ?? "", Align = ContentAlignment };
            Buttons.Add(button);
            return button;
        }

        public IUiInput Input(string text, string initial = "")
        {
            var input = new RecordingConfigInput { Text = text ?? "", Value = initial ?? "", Align = ContentAlignment };
            Inputs.Add(input);
            return input;
        }

        public IUiLabel Label(string text)
        {
            var label = new RecordingConfigLabel { Text = text ?? "", Align = ContentAlignment };
            Labels.Add(label);
            return label;
        }

        public IUiLabel Heading(string text)
        {
            var label = (RecordingConfigLabel)Label(text);
            label.IsHeading = true;
            SectionHeadings.Add(text ?? "");
            return label;
        }

        public IUiLabel Note(string text)
        {
            var label = (RecordingConfigLabel)Label(text);
            label.IsNote = true;
            return label;
        }

        public void BeginInline() => InlineBegan = true;

        public void EndInline() { }

        public IUiToggle Toggle(string text, bool initial = false)
        {
            var toggle = new RecordingConfigToggle { Text = text ?? "", Value = initial, Align = ContentAlignment };
            Toggles.Add(toggle);
            return toggle;
        }

        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();

        public void Show() { }

        public void Hide() { }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingConfigButton : IUiButton
    {
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnClick(Action handler) => this;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingConfigLabel : IUiLabel
    {
        public string Align { get; set; } = SettingsAlign.Centre;
        public bool IsHeading { get; set; }
        public bool IsNote { get; set; }
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingConfigToggle : IUiToggle
    {
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public bool Value { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<bool> handler) => this;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingConfigInput : IUiInput
    {
        private Action<string>? _handler;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<string> handler) { _handler = handler; return this; }
        public void Fire(string text) { Value = text; _handler?.Invoke(text); }
        public void Dispose() => IsDisposed = true;
    }
}
