using System;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// NOPROGMSG1 (user review 2026-10-06: "Don't we have a default? Also it isn't clear … if variables can be used"): the
/// Server tab's Message without Progenitor row says what an empty field sends, names its placeholders, previews the chat
/// line and counts its length. Game-free.
/// </summary>
public sealed class NoProgenitorMessageRowTests
{
    [Fact]
    public void Empty_uses_the_default_which_is_the_fields_placeholder()
    {
        Assert.True(NoProgenitorMessageRow.UsingDefault(""));
        Assert.True(NoProgenitorMessageRow.UsingDefault("   "));
        Assert.Equal(NoProgenitorMessageRow.UsingDefaultLine, NoProgenitorMessageRow.LengthLine(""));
        Assert.Equal(CellTone.Muted, NoProgenitorMessageRow.LengthTone(""));
        // What players see is the default with the sample values filled in.
        string preview = NoProgenitorMessageRow.Preview("");
        Assert.Equal(NoAnswerNoticeText.Build(NoAnswerNoticeText.DefaultTemplate, null, NoProgenitorMessageRow.SampleSeconds), preview);
        Assert.Contains("disconnected in 20 seconds", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("{seconds}", preview, StringComparison.Ordinal);
        var back = NoProgenitorMessageRow.Readback("");
        Assert.Equal(true, back["usingDefault"]);
        Assert.Equal(0, back["chars"]);
        Assert.Equal(preview, back["preview"]);
    }

    [Fact]
    public void A_custom_message_previews_its_placeholders_filled_in_and_the_ascii_rules()
    {
        const string text = "Get <Progenitor> for {mods}; you have {seconds} s.";
        string preview = NoProgenitorMessageRow.Preview(text);
        Assert.Equal("Get (Progenitor) for Progenitor; you have 20 s.", preview);
        Assert.Equal(text.Length + " / 300", NoProgenitorMessageRow.LengthLine(text));
        Assert.Equal(CellTone.Muted, NoProgenitorMessageRow.LengthTone(text));
        var back = NoProgenitorMessageRow.Readback(text);
        Assert.Equal(false, back["usingDefault"]);
        Assert.Equal(text.Length, back["chars"]);
        Assert.Equal(false, back["over"]);
        // Both placeholders are named on the always-visible line.
        Assert.Contains(NoAnswerNoticeText.TokenMods, NoProgenitorMessageRow.PlaceholdersLine, StringComparison.Ordinal);
        Assert.Contains(NoAnswerNoticeText.TokenSeconds, NoProgenitorMessageRow.PlaceholdersLine, StringComparison.Ordinal);
        Assert.Contains("{mods}", NoProgenitorMessageRow.Help, StringComparison.Ordinal);
        Assert.Contains("{seconds}", NoProgenitorMessageRow.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void Over_the_limit_counts_in_the_warning_tone()
    {
        string text = new string('a', NoAnswerNoticeText.MaxTemplateChars + 12);
        Assert.Equal("312 / 300", NoProgenitorMessageRow.LengthLine(text));
        Assert.True(NoProgenitorMessageRow.OverLimit(text));
        Assert.Equal(CellTone.Warn, NoProgenitorMessageRow.LengthTone(text));
        Assert.Equal(true, NoProgenitorMessageRow.Readback(text)["over"]);
        // The chat line itself is cut to the limit, as the server sends it.
        Assert.Equal(NoAnswerNoticeText.MaxTemplateChars, NoProgenitorMessageRow.Preview(text).Length);
        Assert.Equal(CellTone.Muted, NoProgenitorMessageRow.LengthTone(new string('a', NoAnswerNoticeText.MaxTemplateChars)));
    }

    [Fact]
    public void A_drafts_text_is_previewed_before_Apply()
    {
        SettingDrafts.ResetForTests();
        try
        {
            Assert.Null(SettingDrafts.ForRow("p/S/K"));
            SettingDrafts.Note("p/S/K", "Hi {seconds}");
            Assert.Equal("Hi {seconds}", SettingDrafts.ForRow("p/S/K"));
            Assert.Equal("Hi 20", NoProgenitorMessageRow.Preview(SettingDrafts.ForRow("p/S/K")));
        }
        finally { SettingDrafts.ResetForTests(); }
    }

    [Fact]
    public void The_builder_marks_an_empty_default_and_status_lines()
    {
        var file = new BepInEx.Configuration.ConfigFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noprog-" + Guid.NewGuid().ToString("N") + ".cfg"), false);
        var entry = file.Bind("S", "K", "", "d");
        var page = new SettingsPageBuilder("x");
        page.Text(entry, multiline: true).EmptyMeans(NoAnswerNoticeText.DefaultTemplate);
        page.Info("Players see", () => "p").StatusTone(() => CellTone.Warn, () => "help now");
        var items = page.Sections[0].Items;
        Assert.Equal(NoAnswerNoticeText.DefaultTemplate, items[0].Options.Placeholder);
        Assert.Equal(NoAnswerNoticeText.DefaultTemplate, items[0].Options.EmptyShows);
        Assert.Equal(CellTone.Warn, items[1].Options.StatusTone!());
        Assert.Equal("help now", items[1].Options.HelpNow!());
        // Status() is for an info line, EmptyMeans() for a setting.
        Assert.Throws<InvalidOperationException>(() => new SettingsPageBuilder("y").Info("i", () => "").EmptyMeans("d"));
        page.Toggle(file.Bind("S", "T", false, "t"));
        Assert.Throws<InvalidOperationException>(() => page.Status(() => true));
    }
}
