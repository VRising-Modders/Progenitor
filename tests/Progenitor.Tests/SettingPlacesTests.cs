using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Settings organization stage 2 (docs/settings-organization.md, "Overlap: one stored value, many places"): the
/// places table, the <c>settingId</c> / <c>home</c> / <c>alsoAt</c> readback and <c>mods_ui settings_places</c>.
/// Game-free: the ConfigFile shim and the fake UI runtime.
/// </summary>
[Collection("PluginLogger")]
public sealed class SettingPlacesTests : IDisposable
{
    private readonly string _dir;

    public SettingPlacesTests()
    {
        UiApi.ResetForTests();
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        ModsMenu.JoinAdminValueReader = null;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-places-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModsMenu.JoinAdminValueReader = null;
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        ConfigHelper.UninstallAll();
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ConfigFile NewFile(string name) => new(Path.Combine(_dir, name), saveOnInit: true);

    [Fact]
    public void Every_table_row_has_one_id_a_home_and_no_mirror_equal_to_its_home()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in SettingPlaces.All)
        {
            Assert.True(ids.Add(place.SettingId), "listed twice: " + place.SettingId);
            Assert.False(string.IsNullOrEmpty(place.Home));
            Assert.DoesNotContain(place.Home, place.AlsoAt);
            Assert.StartsWith(SettingPlaces.PluginId + "/", place.SettingId, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Install_view_is_at_home_on_the_Preferences_tab_and_mirrored_by_the_Install_toolbar()
    {
        var place = SettingPlaces.Find("progenitor/Mods/InstallView")!;
        Assert.Equal(SettingPlaces.Preferences, place.Home);
        Assert.Equal("preferences", place.Home);
        Assert.Equal(new[] { SettingPlaces.InstallToolbar }, place.AlsoAt);
        Assert.Equal(new[] { SettingPlaces.Preferences, SettingPlaces.InstallToolbar }, place.Places);
        Assert.True(SettingPlaces.HomedOnPreferences(place.SettingId));
        Assert.False(place.OnProgenitorPage);
    }

    [Fact]
    public void The_admin_channel_choices_are_at_home_in_the_panel_with_mirrors_on_the_Server_tab()
    {
        var mid = SettingPlaces.Find(SettingPlaces.JoinAdminId(SettingPlaces.OpMidSession))!;
        Assert.Equal(SettingPlaces.PlayersJoiningPanel, mid.Home);
        Assert.Equal(new[] { SettingPlaces.ModsPlayersNeed }, mid.AlsoAt);
        Assert.Equal("server-tab/mods-players-need", SettingPlaces.ModsPlayersNeed);
        Assert.Equal(SettingPlaces.StoreJoinAdmin, mid.Store);
        var unverified = SettingPlaces.Find(SettingPlaces.JoinAdminId(SettingPlaces.OpUnverified))!;
        Assert.Equal(new[] { SettingPlaces.ModsPlayersNeed }, unverified.AlsoAt);
        Assert.Equal(SettingPlaces.PlayersWithoutProgenitor, SettingPlaces.Find(SettingPlaces.JoinAdminId(SettingPlaces.OpAnswerTimeout))!.Home);
        Assert.Equal(SettingPlaces.OnThisServer, SettingPlaces.Find(SettingPlaces.JoinAdminId(SettingPlaces.OpPhotoMode))!.Home);
        Assert.Equal("progenitor-page/on-this-server", SettingPlaces.OnThisServer);
        Assert.Equal(SettingPlaces.Operator, SettingPlaces.Find(SettingPlaces.JoinAdminId(SettingPlaces.OpAllowedSteamIds))!.Home);
    }

    [Fact]
    public void The_lock_on_the_install_list_and_the_credentials_are_never_drawn()
    {
        foreach (string id in new[]
                 {
                     "progenitor/RemoteInstall/AllowedSteamIdsInGame", "progenitor/Mods/NexusApiKey",
                     "progenitor/Install/LocalIndexPath", "progenitor/Hosted/DryRun",
                 })
        {
            var place = SettingPlaces.Find(id)!;
            Assert.Equal(SettingPlaces.CfgOnly, place.Home);
            Assert.Empty(place.AlsoAt);
            Assert.False(place.OnProgenitorPage);
            Assert.False(SettingPlaces.DrawnOnProgenitorPage(id, fromSharedFile: true));
        }
    }

    [Fact]
    public void Keybinds_resolve_to_their_page_and_the_Keybinds_tab()
    {
        var core = SettingPlaces.Resolve("progenitor/Keybinds/NavigateBack", "progenitor", keybind: true);
        Assert.Equal(SettingPlaces.Keys, core.Home);
        Assert.Equal(new[] { SettingPlaces.KeybindsTab }, core.AlsoAt);
        var mod = SettingPlaces.Resolve("author.mod/Keybinds/Fire", "author.mod", keybind: true);
        Assert.Equal("mod-page/author.mod", mod.Home);
        Assert.Equal(new[] { SettingPlaces.KeybindsTab }, mod.AlsoAt);
        var plain = SettingPlaces.Resolve("author.mod/Menu/ShowHints", "author.mod", keybind: false);
        Assert.Equal("mod-page/author.mod", plain.Home);
        Assert.Empty(plain.AlsoAt);
    }

    [Fact]
    public void Progenitors_page_draws_its_placed_rows_and_unknown_own_rows_but_not_the_shared_files_others()
    {
        Assert.True(SettingPlaces.DrawnOnProgenitorPage("progenitor/PhotoMode/Enabled", fromSharedFile: false));
        // The 2026-10-06 trim: hidden rows are cfg-only, never swept onto the page as unknown own rows.
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/Ui/CaptureMode", fromSharedFile: false));
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/Keybinds/EnforceGameWins", fromSharedFile: false));
        Assert.True(SettingPlaces.DrawnOnProgenitorPage("progenitor/Hosted/Enabled", fromSharedFile: true));
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/Workspace/BackgroundIntensity", fromSharedFile: true));
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/Settings/EditOtherModsSettings", fromSharedFile: false));
        // A new ui.cfg key the table does not know stays on the page (under General); an unknown shared key does not.
        Assert.True(SettingPlaces.DrawnOnProgenitorPage("progenitor/Ui/SomethingNew", fromSharedFile: false));
        Assert.False(SettingPlaces.DrawnOnProgenitorPage("progenitor/Diagnostics/WatchCore", fromSharedFile: true));
    }

    [Fact]
    public void The_readback_keys_name_the_setting_its_home_and_its_mirrors()
    {
        var d = new Dictionary<string, object?>();
        SettingPlaces.AddReadback(d, "progenitor/Mods/InstallView", "progenitor", keybind: false);
        Assert.Equal("progenitor/Mods/InstallView", d["settingId"]);
        Assert.Equal("preferences", d["home"]);
        Assert.Equal(new List<string> { "install-toolbar" }, d["alsoAt"]);
    }

    [Fact]
    public void A_mod_page_setting_row_carries_the_place_keys()
    {
        var file = NewFile("author.mod.cfg");
        var entry = file.Bind("Menu", "ShowHints", true, "Show hints.");
        var row = SettingsRow.ForEntry("author.mod", entry, synced: false);
        var d = ModSettingsView.Describe(row, null, SettingControl.Toggle);
        Assert.Equal("author.mod/Menu/ShowHints", d["settingId"]);
        Assert.Equal("mod-page/author.mod", d["home"]);
        Assert.Equal("true", d["value"]);
    }

    [Fact]
    public void Settings_places_reads_each_value_from_its_one_store_and_leaves_cfg_only_rows_out()
    {
        var shared = NewFile(ConfigHelper.SharedFileName);
        var bindings = ConfigurationPane.Bind(shared);
        SettingsCatalog.Register(SettingPlaces.PluginId, shared, new object(), foreign: true);
        ModsMenu.JoinAdminValueReader = op => op == SettingPlaces.OpMidSession ? ("disconnect", "Disconnect the player") : null;

        var all = Parse(ModsMenu.SettingsPlacesReadback());
        var view = all.GetProperty("progenitor/Mods/InstallView");
        Assert.Equal("Simple", view.GetProperty("value").GetString());
        Assert.Equal("preferences", view.GetProperty("home").GetString());
        Assert.Equal(new[] { "preferences", "install-toolbar" }, view.GetProperty("places").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("cfg", view.GetProperty("store").GetString());
        Assert.Equal("on", all.GetProperty("progenitor/Mods/UseThunderstore").GetProperty("display").GetString());

        var mid = all.GetProperty("progenitor/JoinAdmin/mid-session");
        Assert.Equal("disconnect", mid.GetProperty("value").GetString());
        Assert.Equal("Disconnect the player", mid.GetProperty("display").GetString());
        Assert.True(mid.GetProperty("known").GetBoolean());
        var timeout = all.GetProperty("progenitor/JoinAdmin/answer-timeout");
        Assert.False(timeout.GetProperty("known").GetBoolean());
        Assert.Equal(JsonValueKind.Null, timeout.GetProperty("value").ValueKind);

        Assert.False(all.TryGetProperty("progenitor/Mods/NexusApiKey", out _));
        Assert.False(all.TryGetProperty("progenitor/RemoteInstall/AllowedSteamIdsInGame", out _));

        // The 2026-10-06 trim (user: "No user will care to change those"): listed as cfg-only, with their value still read.
        var intensity = all.GetProperty("progenitor/Workspace/BackgroundIntensity");
        Assert.Equal("cfg", intensity.GetProperty("home").GetString());
        Assert.Empty(intensity.GetProperty("places").EnumerateArray());
        Assert.True(intensity.GetProperty("cfgOnly").GetBoolean());
        Assert.Equal(SettingPlaces.HiddenReason, intensity.GetProperty("hidden").GetString());
        Assert.True(intensity.GetProperty("known").GetBoolean());
        foreach (string id in SettingPlacesHidden)
        {
            Assert.True(all.TryGetProperty(id, out var hidden), id);
            Assert.Equal("cfg", hidden.GetProperty("home").GetString());
            Assert.True(hidden.GetProperty("cfgOnly").GetBoolean(), id);
        }

        // One stored value: a write through one place is what every place reads.
        bindings.InstallView!.Value = "Advanced";
        var again = Parse(ModsMenu.SettingsPlacesReadback());
        Assert.Equal("Advanced", again.GetProperty("progenitor/Mods/InstallView").GetProperty("value").GetString());
    }

    /// <summary>The settings the 2026-10-06 trim moved to the cfg file only.</summary>
    internal static readonly string[] SettingPlacesHidden =
    {
        "progenitor/Workspace/BackgroundIntensity", "progenitor/Workspace/BackgroundContrast",
        "progenitor/Ui/MenuHistoryInnerTabs", "progenitor/Ui/AdminAuthButton",
        "progenitor/Install/CurrentUpdateTag", "progenitor/Ui/CaptureMode",
        "progenitor/Keybinds/EnforceGameWins",
    };

    [Fact]
    public void The_trimmed_settings_are_cfg_only_with_their_keys_kept_and_the_operator_notice_timings_stay()
    {
        foreach (string id in SettingPlacesHidden)
        {
            var place = SettingPlaces.Find(id)!;
            Assert.NotNull(place);
            Assert.Equal(SettingPlaces.CfgOnly, place.Home);
            Assert.Empty(place.AlsoAt);
            Assert.Equal(SettingPlaces.HiddenReason, place.Hidden);
            Assert.False(SettingPlaces.HomedOnPreferences(id));
            Assert.False(place.OnProgenitorPage);
        }

        Assert.Contains("No user will care to change those", SettingPlaces.HiddenReason);
        // RESETWIPE1: the four countdown settings left the menus too, with their own reason; same file and keys.
        foreach (string key in SettingPlaces.CountdownHiddenKeys)
        {
            var place = SettingPlaces.Find("progenitor/ServerMessage/" + key)!;
            Assert.NotNull(place);
            Assert.Equal(SettingPlaces.CfgOnly, place.Home);
            Assert.Empty(place.AlsoAt);
            Assert.Equal(SettingPlaces.CountdownHiddenReason, place.Hidden);
            Assert.Contains("progenitor.servermessage.cfg [ServerMessage] " + key, place.Source);
        }

        foreach (string key in new[] { "Message", "NextReset", "ShowMessage", "ShowModsPage" })
            Assert.Equal(SettingPlaces.WhenPlayersJoin, SettingPlaces.Find("progenitor/ServerMessage/" + key)!.Home);
        // Every other setting has no hidden mark; the Operator notice timings stay drawn on the Server tab (user choice).
        var countdown = SettingPlaces.CountdownHiddenKeys.Select(k => "progenitor/ServerMessage/" + k).ToArray();
        Assert.All(SettingPlaces.All.Where(p => !SettingPlacesHidden.Contains(p.SettingId) && !countdown.Contains(p.SettingId)),
            p => Assert.Equal("", p.Hidden));
        foreach (string op in new[] { SettingPlaces.OpNoticeSeconds, SettingPlaces.OpNoticeRepeatSeconds, SettingPlaces.OpNoticeCreationLimitSeconds })
            Assert.Equal(SettingPlaces.Operator, SettingPlaces.Find(SettingPlaces.JoinAdminId(op))!.Home);
    }

    [Fact]
    public void Find_entry_reaches_the_shared_file_through_the_probe_when_no_source_lists_it()
    {
        var shared = NewFile(ConfigHelper.SharedFileName);
        var hosted = shared.Bind("Hosted", "Enabled", true, "Load Progenitor in private games.");
        Assert.Null(SettingsCatalog.FindEntry(SettingPlaces.PluginId, "Hosted", "Enabled"));
        SettingsCatalog.SharedFileProbe = () => shared;
        Assert.Same(hosted, SettingsCatalog.FindEntry(SettingPlaces.PluginId, "Hosted", "Enabled"));
        Assert.Null(SettingsCatalog.FindEntry("author.mod", "Hosted", "Enabled"));
    }

    private static JsonElement Parse(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
}
