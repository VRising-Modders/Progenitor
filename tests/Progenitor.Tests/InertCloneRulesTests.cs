using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ai;
using Progenitor.Networking;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Core's inert buff clones, game-free half (<see cref="InertCloneRules"/>, <see cref="InertCloneWire"/>,
/// <see cref="InertCloneBook"/>): the fixed registry ids, which prefab a client strips
/// (<see cref="InertCloneRules.Decide"/>: a Buff that is a fixed clone, or an announced haste clone; never an
/// id's shape), the strip lists the live code iterates, the
/// announcement wire, the <c>HideTargetHUD</c> value and its dump flags, and the
/// <see cref="UnitSpawnOptions.HideHealthBar"/> default. The live halves are <c>AiVariants.MakeInert</c>,
/// <c>InertClones</c> and <c>UnitHealthBars</c>; a few source pins tie them to these lists.
/// </summary>
public class InertCloneRulesTests
{
    private static readonly IReadOnlyDictionary<int, InertCloneRole> None = new Dictionary<int, InertCloneRole>();

    [Fact]
    public void Core_clone_ids_are_pinned()
    {
        // Saved units reference these GUIDs (CalculateGuidHash of the id): never change them.
        Assert.Equal("progenitor.core.api-spawned", InertCloneRules.ApiSpawnMarkerId);
        Assert.Equal("progenitor.core.hide-hud", InertCloneRules.HideHudId);
        Assert.Equal("Buff_General_Haste", InertCloneRules.MarkerSource);
    }

    private const int Haste = 900030866;
    private const int ApiGuid = 1111;
    private const int HudGuid = 2222;

    private static InertCloneRole Decide(string? id, int guid, bool isBuff, IReadOnlyDictionary<int, InertCloneRole>? announced, int source, int markerSource = Haste) =>
        InertCloneRules.Decide(id, guid, isBuff, ApiGuid, HudGuid, announced, source, markerSource);

    private static InertCloneBook Announced(int guid, InertCloneRole role = InertCloneRole.Marker)
    {
        var book = new InertCloneBook();
        book.Announce(guid, role);
        return book;
    }

    [Fact]
    public void Core_fixed_clones_are_recognised_by_id_or_guid_without_any_announcement()
    {
        Assert.Equal(InertCloneRole.Marker, Decide(InertCloneRules.ApiSpawnMarkerId, 123, true, None, 0));
        Assert.Equal(InertCloneRole.HideHud, Decide(InertCloneRules.HideHudId, 456, true, null, 0));
        // After a client Core swap the id may be unknown; the fixed GUIDs still match.
        Assert.Equal(InertCloneRole.Marker, Decide(null, ApiGuid, true, None, 0));
        Assert.Equal(InertCloneRole.HideHud, Decide(null, HudGuid, true, None, 0));
    }

    [Fact]
    public void Nothing_without_Buff_is_stripped_not_even_a_fixed_guid()
    {
        Assert.Equal(InertCloneRole.None, Decide(InertCloneRules.ApiSpawnMarkerId, ApiGuid, false, None, Haste));
        Assert.Equal(InertCloneRole.None, Decide(null, 777, false, Announced(777).Roles, Haste));
    }

    [Fact]
    public void An_announced_haste_clone_is_stripped()
    {
        string id = VariantBook.MarkerId("example.demo", "frost-boss_2");
        Assert.Equal(InertCloneRole.Marker, Decide(id, 777, true, Announced(777).Roles, Haste));
        // After a client Core swap: no id, the source the registry remembered.
        Assert.Equal(InertCloneRole.Marker, Decide(null, 777, true, Announced(777).Roles, Haste));
    }

    [Fact]
    public void An_announcement_alone_strips_nothing()
    {
        // Review round 2, finding 2: an announced GUID that is a vanilla prefab (no registry source), an
        // item, or another plugin's clone of some other buff is left alone.
        Assert.Equal(InertCloneRole.None, Decide(null, 777, true, Announced(777).Roles, 0));
        Assert.Equal(InertCloneRole.None, Decide(null, 777, true, Announced(777).Roles, 424242));
        // The marker source could not be resolved on this client: nothing announced is stripped.
        Assert.Equal(InertCloneRole.None, Decide(null, 777, true, Announced(777).Roles, Haste, markerSource: 0));
        Assert.Equal(InertCloneRole.None, Decide(null, 0, true, Announced(777).Roles, Haste));
    }

    [Theory]
    [InlineData("thirdparty.ai.sprint")]      // a plugin's own haste clone with the variant-marker shape
    [InlineData("sample.nodrain")]
    [InlineData("progenitor.core.aura.elite")]  // the ring is the aura's point
    [InlineData("")]
    public void Unannounced_haste_clones_are_left_alone_whatever_their_id(string id)
    {
        var book = Announced(999);
        Assert.Equal(InertCloneRole.None, Decide(id, 555, true, book.Roles, Haste));
        Assert.Equal(InertCloneRole.None, Decide(null, 555, true, book.Roles, Haste));
    }

    [Fact]
    public void Announcements_go_out_before_definitions()
    {
        // Review round 2, finding 1: the client must know to strip before PrfD builds the clone.
        string variants = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "AiVariants.cs");
        int announce = variants.IndexOf("InertClones.Announce(markerGuid", StringComparison.Ordinal);
        int register = variants.IndexOf("ctx.Content.DefineBuff(markerId)", StringComparison.Ordinal);
        Assert.True(announce > 0 && register > announce, "Define must announce before it registers the clone");
        Assert.Contains("InertClones.Forget(markerGuid)", variants);
        string clones = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "InertClones.cs");
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        Assert.Contains("EventBus.On<PlayerConnected>(OnPlayerConnected, priority: 2000)", registry);
        Assert.Contains("EventBus.On<PlayerConnected>(OnPlayerConnected, priority: 2100)", clones);
    }

    [Fact]
    public void Book_records_new_and_changed_announcements_only()
    {
        var book = new InertCloneBook();
        Assert.False(book.Announce(0, InertCloneRole.Marker));
        Assert.False(book.Announce(5, InertCloneRole.None));
        Assert.True(book.Announce(5, InertCloneRole.Marker));
        Assert.False(book.Announce(5, InertCloneRole.Marker));
        Assert.True(book.Announce(5, InertCloneRole.HideHud));
        Assert.Single(book.Snapshot());
        Assert.True(book.Forget(5));
        Assert.False(book.Forget(5));
        Assert.True(book.Announce(5, InertCloneRole.Marker));
        book.Clear();
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Announcement_wire_round_trips_and_rejects_garbage()
    {
        Assert.Equal(0x50724963, InertCloneWire.EventId);
        Assert.True(CustomEventPolicy.IsReserved(InertCloneWire.EventId));
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.SizeOf<InertCloneMessage>());

        Assert.True(InertCloneWire.TryDecode(InertCloneWire.Announce(42, InertCloneRole.Marker), out int guid, out var role, out _));
        Assert.Equal(42, guid);
        Assert.Equal(InertCloneRole.Marker, role);

        var bad = InertCloneWire.Announce(42, InertCloneRole.Marker);
        bad.Schema = 9;
        Assert.False(InertCloneWire.TryDecode(bad, out _, out _, out _));
        Assert.False(InertCloneWire.TryDecode(InertCloneWire.Announce(0, InertCloneRole.Marker), out _, out _, out _));
        Assert.False(InertCloneWire.TryDecode(InertCloneWire.Announce(42, InertCloneRole.None), out _, out _, out _));
        var odd = InertCloneWire.Announce(42, InertCloneRole.Marker);
        odd.Role = 7;
        Assert.False(InertCloneWire.TryDecode(odd, out _, out _, out _));
    }

    [Fact]
    public void Client_strip_is_the_server_strip_plus_the_buff_bar_entry()
    {
        Assert.Equal(InertCloneRules.ServerStrip.Concat(InertCloneRules.ClientExtraStrip), InertCloneRules.ClientStrip);
        Assert.Equal(new[] { "ShowOnBuffBar", "ShowOnBuffBarData" }, InertCloneRules.ClientExtraStrip);
        foreach (string name in InertCloneRules.PrefabOnlyStrip) Assert.Contains(name, InertCloneRules.ServerStrip);
        // The haste client visuals and speed seen live on API-spawned units.
        Assert.Contains("BuffSequence", InertCloneRules.ServerStrip);
        Assert.Contains("Sequencer.BuffSequenceSpawned", InertCloneRules.ServerStrip);
        Assert.Contains("ModifyMovementSpeedBuff", InertCloneRules.ServerStrip);
        Assert.Equal("BuffSequenceSpawned", InertCloneRules.ShortName("Sequencer.BuffSequenceSpawned"));
        Assert.Equal("ShowOnBuffBar", InertCloneRules.ShortName("ShowOnBuffBar"));
    }

    [Fact]
    public void The_live_strip_iterates_these_lists_and_has_a_handler_for_every_name()
    {
        string variants = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "AiVariants.cs");
        Assert.Contains("foreach (string name in InertCloneRules.ServerStrip)", variants);
        foreach (string name in InertCloneRules.ServerStrip)
            Assert.Contains("[\"" + name + "\"] = Handler<", variants);
        string clones = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "InertClones.cs");
        Assert.Contains("foreach (string name in InertCloneRules.ClientExtraStrip)", clones);
    }

    [Fact]
    public void The_client_never_resolves_a_buff_by_spawnable_name()
    {
        // Review R-fix 1: PrefabLookup.TryGetGuidByName reads only the spawnable dictionary, which has no
        // buffs (research buffs.md, evidence 97), so a classification that depended on it never matched.
        // Classification now needs no name lookup at all; keep it that way.
        string clones = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "InertClones.cs");
        Assert.DoesNotContain("TryGetGuidByName", clones);
    }

    [Fact]
    public void Only_the_client_copy_of_the_hide_hud_clone_gets_HideTargetHUD()
    {
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Units", "UnitHealthBars.cs");
        Assert.DoesNotContain("EnsureHideTargetHud(", server);
        string clones = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "InertClones.cs");
        Assert.Contains("role == InertCloneRole.HideHud", clones);
    }

    [Fact]
    public void Hide_target_hud_value_hides_the_character_hud_and_the_target_info()
    {
        // Dump: HideTargetHUDType { None 0, CharacterHUD 1, TargetInfo 2 }, [Flags]; layout.txt declares them.
        Assert.Equal(1, Layout.HideTargetHUDTypeCharacterHUD);
        Assert.Equal(2, Layout.HideTargetHUDTypeTargetInfo);
        Assert.Equal(4, Layout.HideTargetHUDSize);
        Assert.Equal(0, Layout.HideTargetHUDTypeOffset);
        Assert.Equal(-1, InertCloneRules.HideTargetHudType);
        Assert.True(InertCloneRules.HidesHealthBar(InertCloneRules.HideTargetHudType, Layout.HideTargetHUDTypeCharacterHUD, Layout.HideTargetHUDTypeTargetInfo));
        Assert.True(InertCloneRules.HidesHealthBar(3, 1, 2));
        Assert.False(InertCloneRules.HidesHealthBar(1, 1, 2));
        Assert.False(InertCloneRules.HidesHealthBar(2, 1, 2));
        Assert.False(InertCloneRules.HidesHealthBar(0, 1, 2));
    }

    [Fact]
    public void Hide_health_bar_spawn_option_defaults_off()
    {
        Assert.False(new UnitSpawnOptions().HideHealthBar);
        Assert.True(new UnitSpawnOptions { HideHealthBar = true }.HideHealthBar);
    }
}
