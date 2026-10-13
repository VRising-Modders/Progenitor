using System;
using System.IO;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LISTING1 (user review 2026-10-06): the Server listing section's name, its heading help with the update timing, Room
/// left as a counter under the field, and counters and status lines that never light like settings.
/// </summary>
public sealed class ServerListingTests : IDisposable
{
    public void Dispose() => TextTokens.Context = new TextTokens.RenderContext();

    [Fact]
    public void The_section_is_Server_listing_and_the_old_slug_still_resolves()
    {
        Assert.Equal("Server listing", ProgenitorPage.ServerListTitle);
        var groups = new[] { ProgenitorPage.WhenPlayersJoinTitle, ProgenitorPage.ServerListTitle };
        Assert.Equal("Server listing", SectionNav.Resolve(groups, "server-list"));
        Assert.Equal("Server listing", SectionNav.Resolve(groups, "server-listing"));
        Assert.Equal("Server listing", SectionNav.Resolve(groups, "Server listing"));
        Assert.Equal(SettingPlaces.ServerTabPrefix + "server-list", SettingPlaces.ServerList); // the place id is unchanged
    }

    [Fact]
    public void The_heading_help_says_what_players_see_and_when_it_updates()
    {
        string src = Source("Networking", "ServerListDescriptionSettings.cs");
        Assert.Contains("page.Section(PageSection).SectionHelp(SectionHelp);", src);
        Assert.Contains("in-game server browser", src);
        Assert.Contains("the Progenitor signature line", src);
        Assert.Contains("to Steam right away, to EOS within about a second", src);
        Assert.Contains("checked every \"\n        + \"minute", src.Replace("\r\n", "\n"));
        Assert.Contains("the next time a player selects the server", src);
        Assert.Contains("help: \"What the server did with the description last: Steam, EOS and the settings file. \" + UpdateTiming)", src);

        // The registry the description column reads while a heading is hovered.
        try
        {
            ModSettingsSectionHelp.Set("T", "About T.");
            Assert.Equal("About T.", ModSettingsSectionHelp.For("T"));
            Assert.Equal("", ModSettingsSectionHelp.For("Other"));
        }
        finally { ModSettingsSectionHelp.ResetForTests(); }

        string view = Source("Ui", "ModSettingsView.cs");
        Assert.Contains("panel.OnRowHover(heading, entered => SectionHovered(title, entered))", view);
        Assert.Contains("return ModSettingsDescription.Empty(_hoveredSection, sectionHelp);", view);
    }

    [Fact]
    public void Room_left_is_a_counter_under_the_field_before_Insert()
    {
        TextTokens.Context = new TextTokens.RenderContext { ServerListSignature = () => true, Reset = () => null };
        var list = TextTokens.For(TextTokens.ServerListRowId)!;
        string counter = TextTokens.Counter(list, "")!;
        Assert.Equal(ServerListDescription.CounterLine("", true), counter);
        Assert.Contains("characters left", counter, StringComparison.Ordinal);
        Assert.Null(TextTokens.Counter(TextTokens.For(TextTokens.MessageRowId)!, "x"));

        string view = Source("Ui", "ModSettingsView.cs");
        int counterRow = view.IndexOf("counterRow = panel.CellRow(ConfigurationPane.StatusRowSpec(", StringComparison.Ordinal);
        int insert = view.IndexOf("var cellRow = panel.CellRow(spec);", StringComparison.Ordinal);
        Assert.True(counterRow > 0 && insert > counterRow, "counter, then Insert");
        Assert.DoesNotContain("page.Info(\"Room left\"", Source("Networking", "ServerListDescriptionSettings.cs"));
    }

    /// <summary>Counters and status lines (Room left, Length, Countdown now, Status) are info rows: never lit.</summary>
    [Fact]
    public void Non_interactive_rows_do_not_get_the_highlight()
    {
        Assert.False(ModSettingsLayout.TakesHighlight(ModSettingsItemKind.Info, SettingControl.ReadOnly));
        Assert.False(ModSettingsLayout.TakesHighlight(ModSettingsItemKind.Info, SettingControl.Text));
        Assert.True(ModSettingsLayout.TakesHighlight(ModSettingsItemKind.Setting, SettingControl.Toggle));
        Assert.True(ModSettingsLayout.TakesHighlight(ModSettingsItemKind.Setting, SettingControl.ReadOnly));
        Assert.True(ModSettingsLayout.TakesHighlight(ModSettingsItemKind.Button, SettingControl.ReadOnly));
        Assert.Contains("main.SetRowLit(lit && ModSettingsLayout.TakesHighlight(binding.Item.Kind, binding.Control));", Source("Ui", "ModSettingsView.cs"));
    }

    private static string Source(string dir, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Progenitor.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "Progenitor.Core", dir, file));
    }
}
