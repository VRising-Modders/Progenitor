using Progenitor.Cameras;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Lock-on names (live finding 2026-10-02: the lock showed CHAR_Critter_Rat). Order: player name,
/// the localized ManagedCharacterHUD name the target panel shows, then a prettified prefab name.
/// </summary>
public class PhotoUnitNameTests
{
    [Fact]
    public void A_player_is_named_by_their_character_name()
    {
        var n = PhotoUnitNames.Choose("TestAdmin", "Vampire", "CHAR_VampireMale", "45:1");
        Assert.Equal("TestAdmin", n.Display);
        Assert.Equal("player", n.Source);
        Assert.Equal("CHAR_VampireMale", n.Prefab);
    }

    [Fact]
    public void A_unit_is_named_by_its_localized_name_with_the_prefab_kept_separately()
    {
        var n = PhotoUnitNames.Choose(null, "Rat", "CHAR_Critter_Rat", "10:1");
        Assert.Equal("Rat", n.Display);
        Assert.Equal("localized", n.Source);
        Assert.Equal("CHAR_Critter_Rat", n.Prefab);

        var boss = PhotoUnitNames.Choose("", "Rufus the Foreman", "CHAR_Bandit_Foreman_VBlood", "20:3");
        Assert.Equal("Rufus the Foreman", boss.Display);
    }

    [Theory]
    [InlineData("CHAR_Critter_Rat", "Rat")]
    [InlineData("CHAR_Bandit_Foreman_VBlood", "Bandit Foreman")]
    [InlineData("CHAR_Undead_SkeletonCrossbow_Base", "Undead Skeleton Crossbow Base")]
    [InlineData("CHAR_Wildlife_Wolf", "Wolf")]
    [InlineData("CHAR", "CHAR")]
    [InlineData("", "")]
    public void Without_a_localized_name_the_prefab_name_is_prettified(string prefab, string expected) =>
        Assert.Equal(expected, PhotoUnitNames.Prettify(prefab));

    [Fact]
    public void The_fallbacks_say_where_the_name_came_from()
    {
        var pretty = PhotoUnitNames.Choose(null, null, "CHAR_Critter_Rat", "10:1");
        Assert.Equal("Rat", pretty.Display);
        Assert.Equal("prefab", pretty.Source);

        var id = PhotoUnitNames.Choose(null, "  ", null, "10:1");
        Assert.Equal("10:1", id.Display);
        Assert.Equal("id", id.Source);
        Assert.Equal("", id.Prefab);
    }

    [Fact]
    public void The_client_reads_the_hud_name_through_the_managed_registry_and_the_string_table()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        SourcePin.AssertUncommented(src, "if (registry != null && registry.TryGet<ManagedCharacterHUD>(guid, out var hud) && hud != null");
        SourcePin.AssertUncommented(src, "&& Localization.Localization.TryGetGameString(hud.Name, out var s))");
        SourcePin.AssertUncommented(src, "player = em.GetComponentData<PlayerCharacter>(e).Name.ToString();");
        SourcePin.AssertUncommented(src, "[\"prefab\"] = n.Prefab,");
    }
}
