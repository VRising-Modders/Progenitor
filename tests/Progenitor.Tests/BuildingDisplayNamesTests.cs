using System.Collections.Generic;
using Progenitor.Castles;
using Xunit;

namespace Progenitor.Tests;

public class BuildingDisplayNamesTests
{
    private const int Devourer = -1000;

    public BuildingDisplayNamesTests() => BuildingDisplayNames.Clear();

    [Fact]
    public void A_found_name_is_returned_and_kept_for_the_world()
    {
        var world = new object();
        int calls = 0;
        (BuildingNameLookup, string?) Lookup(int p)
        {
            calls++;
            return (BuildingNameLookup.Found, " The Devourer ");
        }

        Assert.Equal("The Devourer", BuildingDisplayNames.Get(Devourer, world, Lookup));
        Assert.Equal("The Devourer", BuildingDisplayNames.Get(Devourer, world, Lookup));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_known_absence_is_kept_as_null_and_data_not_up_yet_is_asked_again()
    {
        var world = new object();
        var answers = new Queue<(BuildingNameLookup, string?)>(new[]
        {
            (BuildingNameLookup.NotReady, (string?)null),
            (BuildingNameLookup.NotReady, (string?)null),
            (BuildingNameLookup.NoName, (string?)null),
        });
        int calls = 0;
        (BuildingNameLookup, string?) Lookup(int p)
        {
            calls++;
            return answers.Dequeue();
        }

        Assert.Null(BuildingDisplayNames.Get(7, world, Lookup));
        Assert.Null(BuildingDisplayNames.Get(7, world, Lookup));
        Assert.Null(BuildingDisplayNames.Get(7, world, Lookup));
        Assert.Null(BuildingDisplayNames.Get(7, world, Lookup));
        Assert.Equal(3, calls);
        Assert.Equal(1, BuildingDisplayNames.Count);
    }

    [Fact]
    public void A_blank_found_name_is_no_name()
    {
        var world = new object();
        Assert.Null(BuildingDisplayNames.Get(9, world, _ => (BuildingNameLookup.Found, "  ")));
        Assert.Equal(1, BuildingDisplayNames.Count);
    }

    [Fact]
    public void A_world_change_forgets_every_name()
    {
        var first = new object();
        var second = new object();
        Assert.Equal("A", BuildingDisplayNames.Get(1, first, _ => (BuildingNameLookup.Found, "A")));
        Assert.Equal("B", BuildingDisplayNames.Get(1, second, _ => (BuildingNameLookup.Found, "B")));
        Assert.Equal(1, BuildingDisplayNames.Count);
    }

    [Fact]
    public void Prefab_zero_no_world_and_a_throwing_lookup_give_null_and_keep_nothing()
    {
        var world = new object();
        Assert.Null(BuildingDisplayNames.Get(0, world, _ => (BuildingNameLookup.Found, "x")));
        Assert.Null(BuildingDisplayNames.Get(1, null, _ => (BuildingNameLookup.Found, "x")));
        Assert.Null(BuildingDisplayNames.Get(1, world, _ => throw new System.InvalidOperationException()));
        Assert.Equal(0, BuildingDisplayNames.Count);
    }

    [Fact]
    public void The_client_lookup_reads_the_blueprint_name_through_the_game_string_table()
    {
        // Source pin on the Unity half (not compiled here).
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Castles", "PluginCastlesIcons.cs");
        Assert.Contains("registry.TryGet<ProjectM.ManagedBlueprintData>(", src);
        Assert.Contains("Localization.Localization.TryGetGameString(key, out var text)", src);
        Assert.Contains("if (prefabGuid == 0 || Plugin.IsServerProcess) return null;", src);
    }
}
