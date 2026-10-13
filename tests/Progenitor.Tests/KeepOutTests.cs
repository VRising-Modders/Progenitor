using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// KEEPOUT1 (decided 2026-10-06): Players without Progenitor, Let them play (default) / Keep them out, and the copy
/// fixes around it (COPY2).
/// </summary>
public sealed class KeepOutTests : IDisposable
{
    public void Dispose() => Handshake.KeepOutProbe = null;

    /// <summary>The decision matrix: keep out × a server plugin that needs Progenitor × a policy that needs an answer × 0/N.</summary>
    [Theory]
    [InlineData(false, false, false, 20, false, 20)]
    [InlineData(false, true, false, 20, true, 20)]
    [InlineData(false, false, true, 20, true, 20)]
    [InlineData(false, true, true, 20, true, 20)]
    [InlineData(true, false, false, 20, true, 20)]
    [InlineData(true, true, true, 20, true, 20)]
    [InlineData(false, true, false, 0, true, 0)] // Let them play with 0: no deadline, as before
    [InlineData(true, false, false, 0, true, NoProgenitorKeepOut.KeepOutMinSeconds)] // Keep them out with 0: still kept out
    [InlineData(true, true, false, 0, true, NoProgenitorKeepOut.KeepOutMinSeconds)]
    [InlineData(true, false, false, -5, true, NoProgenitorKeepOut.KeepOutMinSeconds)]
    public void Who_is_disconnected_and_after_how_long(bool keepOut, bool plugin, bool policy, int configured, bool disconnects, int timeout)
    {
        Assert.Equal(disconnects, NoProgenitorKeepOut.Disconnects(keepOut, plugin, policy));
        Assert.Equal(timeout, NoProgenitorKeepOut.Timeout(keepOut, configured));
    }

    [Fact]
    public void Handshake_applies_the_choice_where_it_decides_the_deadline()
    {
        Handshake.KeepOutProbe = () => false;
        Assert.False(Handshake.KeepOut());
        Assert.False(Handshake.RequiresAnswer(Array.Empty<Handshake.ModRecord>()));
        Assert.Equal(0, Handshake.AnswerTimeoutFor(0));
        Handshake.KeepOutProbe = () => true;
        Assert.True(Handshake.RequiresAnswer(Array.Empty<Handshake.ModRecord>()));
        Assert.Equal(NoProgenitorKeepOut.KeepOutMinSeconds, Handshake.AnswerTimeoutFor(0));
        Assert.Equal(30, Handshake.AnswerTimeoutFor(30));
        Assert.Equal(NoProgenitorKeepOut.KeepOutSummary, Handshake.RequiredSummary(Array.Empty<Handshake.ModRecord>()));
        Handshake.KeepOutProbe = () => throw new InvalidOperationException();
        Assert.False(Handshake.KeepOut()); // a broken probe is the default, Let them play

        string src = Source("Networking", "Handshake.cs");
        Assert.Contains("int timeout = AnswerTimeoutFor(ReadAnswerTimeoutSeconds());", src);
        Assert.Contains("NoProgenitorKeepOut.Disconnects(KeepOut(), Compare.ServerRequiresAnswer(required), SafePolicy()?.RequiresAnswer ?? false);", src);
        Assert.Contains("ConfigHelper.Bind(file, \"Networking\", NoProgenitorKeepOut.ConfigKey, NoProgenitorKeepOut.Default,", src);
    }

    /// <summary>The cfg entry, the admin-channel option, its place and its row (the bridge reads the row's choices).</summary>
    [Fact]
    public void The_option_is_stored_in_networking_and_drawn_as_a_radio_list()
    {
        var spec = ServerOptions.Find(ServerOptionOps.RequireProgenitor)!;
        Assert.Equal("Networking", spec.Section);
        Assert.Equal("RequireProgenitor", spec.Key);
        Assert.Equal(ServerOptionKind.Switch, spec.Kind);
        Assert.False(NoProgenitorKeepOut.Default);
        Assert.Contains(ServerOptionOps.RequireProgenitor, JoinAdminOps.All);
        Assert.Contains(ServerOptionOps.RequireProgenitor, ServerOptionOps.All);
        Assert.Equal(SettingPlaces.PlayersWithoutProgenitor, SettingPlaces.Find(SettingPlaces.JoinAdminId(ServerOptionOps.RequireProgenitor))!.Home);
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id("Networking", "RequireProgenitor"))!.Home);
        Assert.Equal(SettingPlaces.JoinAdminId(ServerOptionOps.RequireProgenitor), ServerSettingsLock.JoinAdminSettingId(ServerOptionOps.RequireProgenitor));

        var row = JoinAdminRows.KeepOut();
        Assert.True(row.ChoiceRadio);
        Assert.Equal(new[] { "false", "true" }, row.Choices.ToArray());
        Assert.Equal(new[] { "Let them play", "Keep them out" }, row.ChoiceLabels.ToArray());
        Assert.Contains("unless this server's mods need Progenitor", row.ChoiceDescriptions[0], StringComparison.Ordinal);
        Assert.Contains("Message without Progenitor", row.ChoiceDescriptions[1], StringComparison.Ordinal);
        Assert.All(row.ChoiceDescriptions, d => Assert.InRange(UiRadioList.AboutLines(d, DialogLayout.ContentRowWidth()), 1, UiRadioList.MaxAboutLines));
        string page = Source("Ui", "ProgenitorPage.cs");
        Assert.True(page.IndexOf("JoinAdminRows.KeepOut()", StringComparison.Ordinal) < page.IndexOf("JoinAdminRows.AnswerTimeout()", StringComparison.Ordinal));
    }

    [Fact]
    public void The_copy_says_what_each_choice_does_and_when_it_applies()
    {
        Assert.Equal("If a player's mods break the rules while playing", ClientPolicyText.MidSessionHeading);
        Assert.Equal("Mods break rules while playing", JoinAdminRows.MidSessionLabel);
        Assert.Equal("Pause the mod and warn them", ClientPolicyText.MidSessionWarn);
        Assert.Equal("Disconnect them", ClientPolicyText.MidSessionDisconnect);
        Assert.Equal("A mod you don't allow is paused if Progenitor can. Otherwise, or when a required mod is removed or turned off, the player gets "
            + "a notice. Nobody is disconnected; their next join is refused until it's fixed.",
            ClientPolicyText.MidSessionWarnShort);
        Assert.Equal("Applies to a mod you don't allow and to a required mod removed or turned off while playing. The player gets a notice naming the mod "
            + "and is disconnected after 60 seconds unless they fix it.",
            ClientPolicyText.MidSessionDisconnectShort);
        Assert.Contains("a mod it requires is removed or turned off", JoinAdminRows.MidSessionHelp, StringComparison.Ordinal);
        // REQRULE1: the save note after switching to Disconnect covers required mods too.
        Assert.Contains("remove or turn off a required one", ClientPolicyText.MidSessionDisconnectNote, StringComparison.Ordinal);

        Assert.Contains("can't join until they install it", ClientPolicyText.RefuseThemShort, StringComparison.Ordinal);
        // REQRULE7: plain words with an example; no "stored value"; Keep them out names the relationship too.
        Assert.Contains("Example: your server allows only a minimap and a chat mod.", ClientPolicyText.UnverifiedHelp, StringComparison.Ordinal);
        Assert.Contains("This only matters with Only approved mods.", ClientPolicyText.UnverifiedHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("stored value", JoinAdminRows.Unverified().Help, StringComparison.Ordinal);
        Assert.DoesNotContain("stored value", JoinAdminRows.MidSession().Help, StringComparison.Ordinal);
        Assert.Contains("If mods can't be checked", JoinAdminRows.KeepOutHelp, StringComparison.Ordinal);
        Assert.Contains("unchecked", ClientPolicyText.LetThemJoinShort, StringComparison.Ordinal);
        Assert.Contains("Only approved mods", ClientPolicyText.RefuseThemShort, StringComparison.Ordinal);
        Assert.Contains("Keep them out", ClientPolicyText.UnverifiedHelp, StringComparison.Ordinal);
        // Only approved + Refuse them is what makes it bite (ClientModPolicy.RequiresAnswer).
        Assert.Contains("Mode == ClientPolicyMode.Approved && Unverified == UnverifiedPolicy.Refuse", Source("Networking", "ClientModPolicy.cs"));

        Assert.Contains("Keep them out", JoinAdminRows.AnswerTimeoutHelp, StringComparison.Ordinal);
        Assert.Contains("needs Progenitor on clients", JoinAdminRows.AnswerTimeoutHelp, StringComparison.Ordinal);
        Assert.InRange(UiRadioList.AboutLines(ClientPolicyText.RefuseThemShort, DialogLayout.ContentRowWidth()), 1, 2);
        // REQRULE1: the mid-session lines name required mods too, so they take up to three lines (four is the list's limit).
        foreach (string d in new[] { ClientPolicyText.MidSessionWarnShort, ClientPolicyText.MidSessionDisconnectShort })
            Assert.InRange(UiRadioList.AboutLines(d, DialogLayout.ContentRowWidth()), 1, 3);
    }

    [Fact]
    public void If_mods_cant_be_checked_says_it_is_not_in_effect_under_any_mods()
    {
        try
        {
            // REQRULE7: grayed (read-only) with the one-line reason under Any mods.
            Assert.Equal("Only applies with Only approved mods.", JoinAdminRows.UnverifiedInactive);
            JoinAdminRows.StateProbe = () => new JoinAdminState { PolicySupported = true, PolicyMode = "any" };
            Assert.Equal(JoinAdminRows.UnverifiedInactive, JoinAdminRows.Unverified().ReadOnlyNow());
            JoinAdminRows.StateProbe = () => new JoinAdminState { PolicySupported = true, PolicyMode = "approved" };
            Assert.Null(JoinAdminRows.Unverified().ReadOnlyNow());
            JoinAdminRows.StateProbe = null;
            Assert.Null(JoinAdminRows.Unverified().ReadOnlyNow());
        }
        finally { JoinAdminRows.StateProbe = null; }
    }

    private static string Source(string dir, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Progenitor.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "Progenitor.Core", dir, file));
    }
}
