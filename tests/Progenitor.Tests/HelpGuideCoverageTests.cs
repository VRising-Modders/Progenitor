using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Progenitor.Ui;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

/// <summary>
/// The help guide (docs/guide/progenitor-guide.html) names every row Progenitor draws on its own Mods page (Your settings,
/// On this server), in the Server tab and in Preferences, by the label the player sees. A new Progenitor setting, button
/// or admin row fails here until the guide's reference chapters describe it (docs/documentation.md, "Keep the help guide
/// current"). Not covered: keybind names (generated from ids) and the Server tab rows their own modules add. Game-free.
/// </summary>
public sealed class HelpGuideCoverageTests
{
    private readonly ITestOutputHelper _out;

    public HelpGuideCoverageTests(ITestOutputHelper output) => _out = output;

    /// <summary>The labels of Progenitor's own rows, as the builders declare them.</summary>
    internal static List<string> ProgenitorRowLabels()
    {
        var page = new SettingsPageBuilder(SettingPlaces.PluginId);
        ProgenitorPage.BuildYourSettings(page);
        ProgenitorPage.BuildOnThisServer(page);
        ProgenitorPage.BuildServerTab(page, new ProgenitorPage.ServerParts());

        var labels = new List<string>();
        foreach (var section in page.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Title)) labels.Add(section.Title);
            foreach (var item in section.Items)
            {
                if (item.Kind == ModSettingsItemKind.Divider || item.AllKeybinds) continue;
                string label = item.Admin?.Label ?? item.Options?.Label ?? item.Label;
                if (string.IsNullOrWhiteSpace(label)) label = item.Label;
                if (!string.IsNullOrWhiteSpace(label)) labels.Add(label);
            }
        }

        // The Preferences tab's rows (ConfigurationPane), by their label constants.
        labels.AddRange(new[]
        {
            ConfigurationPane.EditOtherModsLabel, ConfigurationPane.InstallViewLabel, ConfigurationPane.RememberFiltersLabel,
            ConfigurationPane.ShowNsfwLabel, ConfigurationPane.DownloadLimitLabel, ConfigurationPane.AdvancedLabel,
            ConfigurationPane.IndexPathLabel, ConfigurationPane.CanonicalFrameLabel, ConfigurationPane.UpdateModsLabel,
            ConfigurationPane.MajorVersionsLabel, ConfigurationPane.PromptBeforeJoinLabel, ConfigurationPane.UpdateProgenitorLabel,
            ConfigurationPane.ProgenitorServersLabel,
        });

        return labels.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The guide's content block as plain text: tags dropped, entities decoded, spaces folded.</summary>
    internal static string GuideText()
    {
        string html = SourcePin.ReadFile("docs", "guide", "progenitor-guide.html");
        int a = html.IndexOf("<!-- HELP-CONTENT START -->", StringComparison.Ordinal);
        int b = html.IndexOf("<!-- HELP-CONTENT END -->", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "the guide has no content block");
        string block = html.Substring(a, b - a);
        block = Regex.Replace(block, @"\[\[[^|\]]+\|([^\]]+)\]\]", "$1");
        block = Regex.Replace(block, "<[^>]+>", " ");
        block = WebUtility.HtmlDecode(block).Replace("\\\"", "\"");
        return Normalize(block);
    }

    internal static string Normalize(string text) =>
        Regex.Replace(text.Replace('’', '\'').Replace(' ', ' '), @"\s+", " ").Trim();

    [Fact]
    public void Every_row_on_Progenitors_page_and_the_Server_tab_is_named_in_the_help_guide()
    {
        var labels = ProgenitorRowLabels();
        Assert.NotEmpty(labels);
        string guide = GuideText();
        var missing = labels.Where(l => guide.IndexOf(Normalize(l), StringComparison.OrdinalIgnoreCase) < 0).ToList();
        foreach (string l in labels) _out.WriteLine((missing.Contains(l) ? "MISSING  " : "ok       ") + l);
        Assert.True(missing.Count == 0,
            "The help guide (docs/guide/progenitor-guide.html) does not name these rows; add them to its reference chapters: "
            + string.Join(" | ", missing));
    }
}
