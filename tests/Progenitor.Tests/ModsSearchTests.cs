using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;
namespace Progenitor.Tests;
public sealed class ModsSearchTests
{
    [Fact]
    public void Searches_builder_help_and_preserves_original_options_and_scope()
    {
        var options = new ModSettingOptions { Help = "Fill castle hearts", EnableIf = () => false };
        var item = ModSettingsItem.ForInfo("sample", "Top up", () => "off", 0, options);
        var section = new ModSettingsSection("Automation", ModSettingsSectionKind.Page, new List<ModSettingsItem> { item })
            { Scope = SettingScope.ServerWide, Owner = SettingOwner.Server };
        var found = Assert.Single(ModsMenu.SearchSections(new[] { section }, "HEARTS"));
        Assert.Same(item, Assert.Single(found.Items));
        Assert.Equal(section.Scope, found.Scope);
        Assert.Equal(section.Owner, found.Owner);
    }
    [Fact]
    public void Hidden_settings_do_not_leak_into_search_and_section_names_match()
    {
        var visible = ModSettingsItem.ForInfo("sample", "Visible", () => "", 0, new ModSettingOptions());
        var hidden = ModSettingsItem.ForInfo("sample", "Hidden", () => "", 0, new ModSettingOptions { ShowIf = () => false });
        var section = new ModSettingsSection("Castle", ModSettingsSectionKind.Page, new List<ModSettingsItem> { visible, hidden });
        Assert.Same(visible, Assert.Single(Assert.Single(ModsMenu.SearchSections(new[] { section }, "castle")).Items));
        Assert.Empty(ModsMenu.SearchSections(new[] { section }, "hidden"));
    }
}
