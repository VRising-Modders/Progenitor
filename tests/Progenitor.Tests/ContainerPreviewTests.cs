using System;
using System.Text;
using Progenitor.Cameras;
using Progenitor.Castles;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the remote container preview (<c>ContainerPreviewPolicy.cs</c>, <c>ContainerPreviewWire.cs</c>)
/// and the shared streaming-origin lease (<c>ObservationShares.cs</c>): Core's owner-or-clan authorization and its
/// order, the client state machine with every timeout and close reason, the HUD auto-close gate, Escape precedence,
/// the wire's direction rule, and how holders share, expire and end one lease. The game reads that feed these are
/// client and server runtime code, not live-verified.
/// </summary>
public class ContainerPreviewTests
{
    // ---- authorization -------------------------------------------------------------------------

    private static ContainerPreviewFacts Allowed(CastleRelation relation = CastleRelation.Owner) => new()
    {
        UserConnected = true, Mode = ContainerPreviewMode.On, RateOk = true, IdParsed = true, Found = true, HasInventory = true,
        HasOwnMenu = false, InCastle = true, Relation = relation, HasNetworkId = true,
    };

    [Theory]
    [InlineData(CastleRelation.Owner)]
    [InlineData(CastleRelation.ClanMember)]
    public void Owner_and_clan_may_preview_a_plain_container(CastleRelation relation) =>
        Assert.Null(ContainerPreviewAuthorization.Refusal(Allowed(relation)));

    [Theory]
    [InlineData(CastleRelation.Allied)]
    [InlineData(CastleRelation.None)]
    public void Allies_and_strangers_are_refused(CastleRelation relation)
    {
        var why = ContainerPreviewAuthorization.Refusal(Allowed(relation));
        Assert.Equal("Only the castle's owner and clan can preview its containers.", why);
    }

    [Fact]
    public void A_station_heart_or_coffin_is_refused_even_for_the_owner()
    {
        var f = Allowed();
        f.HasOwnMenu = true;
        Assert.Equal("Only plain containers (chests, braziers) can be previewed.", ContainerPreviewAuthorization.Refusal(f));
    }

    [Fact]
    public void Each_missing_fact_has_its_own_refusal()
    {
        var f = Allowed(); f.HasInventory = false;
        Assert.Equal("That has no inventory.", ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.InCastle = false;
        Assert.Equal("That container is not in a castle.", ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.Found = false;
        Assert.Equal("That container no longer exists.", ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.IdParsed = false;
        Assert.Equal("That is not a container id.", ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.RateOk = false;
        Assert.Equal("Too many preview requests; try again shortly.", ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.HasNetworkId = false;
        Assert.Equal("That container has no network id yet.", ContainerPreviewAuthorization.Refusal(f));
    }

    [Fact]
    public void The_order_is_fixed_connection_first_and_relation_after_the_container_checks()
    {
        var f = new ContainerPreviewFacts { Relation = CastleRelation.Allied, HasOwnMenu = true };
        Assert.Equal("You are not connected.", ContainerPreviewAuthorization.Refusal(f));
        // A stranger asking about a station learns it is a station before anything about the castle.
        f = Allowed(CastleRelation.None); f.HasOwnMenu = true; f.InCastle = false;
        Assert.Equal("Only plain containers (chests, braziers) can be previewed.", ContainerPreviewAuthorization.Refusal(f));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(600, 600)]
    [InlineData(5000, 600)]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(double.NaN, 30)]
    [InlineData(double.PositiveInfinity, 30)]
    public void Granted_seconds_are_clamped(double requested, double granted) =>
        Assert.Equal(granted, ContainerPreviewAuthorization.GrantSeconds(requested));

    // ---- the server setting --------------------------------------------------------------------

    [Fact]
    public void The_setting_defaults_to_PvE_servers_only()
    {
        Assert.Equal(ContainerPreviewSettings.WordPvE, ContainerPreviewSettings.DefaultWord);
        Assert.Equal(ContainerPreviewMode.PvEOnly, ContainerPreviewSettings.Parse(null));
        Assert.Equal(ContainerPreviewMode.PvEOnly, ContainerPreviewSettings.Parse("nonsense"));
        Assert.Equal(ContainerPreviewMode.Off, ContainerPreviewSettings.Parse(" Off "));
        Assert.Equal(ContainerPreviewMode.On, ContainerPreviewSettings.Parse("ON"));
        Assert.Equal(new[] { "off", "pve", "on" }, ContainerPreviewSettings.Words);
        Assert.Equal(3, ContainerPreviewSettings.Labels.Count);
        Assert.Equal(3, ContainerPreviewSettings.Descriptions.Count);
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, null, true)]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, null, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    public void The_setting_allows_by_mode_and_game_mode(int mode, bool? pve, bool allowed) =>
        Assert.Equal(allowed, ContainerPreviewSettings.Refusal((ContainerPreviewMode)mode, pve) == null);

    [Fact]
    public void A_server_with_the_setting_off_tells_the_player_so_before_anything_else_about_the_chest()
    {
        var f = Allowed(); f.Mode = ContainerPreviewMode.Off; f.Found = false; f.RateOk = false;
        Assert.Equal(ContainerPreviewSettings.OffRefusal, ContainerPreviewAuthorization.Refusal(f));
        f = Allowed(); f.Mode = ContainerPreviewMode.PvEOnly; f.IsPvE = false;
        Assert.Equal(ContainerPreviewSettings.PvPRefusal, ContainerPreviewAuthorization.Refusal(f));
        Assert.Contains("turned off", ContainerPreviewSettings.OffRefusal);
        f = Allowed(); f.Mode = ContainerPreviewMode.PvEOnly; f.IsPvE = true;
        Assert.Null(ContainerPreviewAuthorization.Refusal(f));
        f = new ContainerPreviewFacts { Mode = ContainerPreviewMode.Off };
        Assert.Equal("You are not connected.", ContainerPreviewAuthorization.Refusal(f));
    }

    [Fact]
    public void The_admin_option_is_a_three_way_choice_stored_in_the_shared_cfg()
    {
        var spec = Progenitor.Networking.ServerOptions.Find(Progenitor.Networking.ServerOptionOps.ContainerPreview);
        Assert.NotNull(spec);
        Assert.Equal(Progenitor.Networking.ServerOptionKind.Choice, spec!.Kind);
        Assert.Equal(ContainerPreviewSettings.Section, spec.Section);
        Assert.Equal(ContainerPreviewSettings.ModeKey, spec.Key);
        Assert.Equal(ContainerPreviewSettings.Words, spec.Choices);
        Assert.Contains(Progenitor.Networking.ServerOptionOps.ContainerPreview, Progenitor.Networking.ServerOptionOps.All);
    }

    // ---- what is a plain container ---------------------------------------------------------------

    [Fact]
    public void Stations_with_their_own_menus_are_on_the_refusal_list()
    {
        foreach (var name in new[]
                 {
                     "CastleHeart", "Refinementstation", "WorkstationRecipesBuffer", "ResearchStation", "Salvagestation",
                     "UnitSpawnerstation", "PrisonCell", "ServantCoffinstation", "ServantEquipment", "Forge_Shared",
                     "FusionForge_Shared", "BloodMixer_Shared", "JewelCraftingStation", "Stables_Shared",
                     "CastleArenaStation_Shared", "InventoryRouteStation",
                 })
            Assert.Contains(name, ContainerPreviewMenus.OwnMenuComponents);
        Assert.DoesNotContain("BurnContainer", ContainerPreviewMenus.OwnMenuComponents);   // braziers are plain containers
    }

    [Fact]
    public void The_server_checks_every_name_on_the_refusal_list_and_nothing_else()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        int start = source.IndexOf("private static bool HasOwnMenu(", StringComparison.Ordinal);
        Assert.True(start > 0);
        string body = source.Substring(start, source.IndexOf(";", start, StringComparison.Ordinal) - start);
        var checks = System.Text.RegularExpressions.Regex.Matches(body, @"Has<(?:[A-Za-z0-9_]+\.)*([A-Za-z0-9_]+)>\(em\)");
        var names = new System.Collections.Generic.List<string>();
        foreach (System.Text.RegularExpressions.Match m in checks) names.Add(m.Groups[1].Value);
        Assert.Equal(ContainerPreviewMenus.OwnMenuComponents, names);
    }

    // ---- telling the server ----------------------------------------------------------------------

    [Theory]
    [InlineData(ContainerPreviewCloseReason.Unloaded, true)]
    [InlineData(ContainerPreviewCloseReason.NoAnswer, true)]
    [InlineData(ContainerPreviewCloseReason.Disposed, true)]
    [InlineData(ContainerPreviewCloseReason.Escape, true)]
    [InlineData(ContainerPreviewCloseReason.WindowClosed, true)]
    [InlineData(ContainerPreviewCloseReason.ReplicationTimeout, true)]
    [InlineData(ContainerPreviewCloseReason.OpenFailed, true)]
    [InlineData(ContainerPreviewCloseReason.Timeout, true)]
    [InlineData(ContainerPreviewCloseReason.TargetGone, true)]
    [InlineData(ContainerPreviewCloseReason.Replaced, true)]
    [InlineData(ContainerPreviewCloseReason.Failed, true)]
    [InlineData(ContainerPreviewCloseReason.Refused, false)]
    [InlineData(ContainerPreviewCloseReason.LeaseEnded, false)]
    [InlineData(ContainerPreviewCloseReason.WorldChanged, false)]
    public void Every_close_but_the_servers_own_and_a_lost_connection_releases_the_lease(ContainerPreviewCloseReason reason, bool tells) =>
        Assert.Equal(tells, ContainerPreviewMachine.TellsServer(reason));

    [Fact]
    public void The_client_sends_close_through_the_one_rule()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewClient.cs");
        Assert.Contains("if (tellServer && ContainerPreviewMachine.TellsServer(reason))", source);
    }

    // ---- the mapper guard after close -----------------------------------------------------------

    [Fact]
    public void The_mapper_guard_stays_until_the_window_is_gone_or_two_seconds_pass()
    {
        Assert.False(ContainerPreviewMachine.MapperGuardDone(worldAlive: true, windowStillOnTarget: true, 0.5));
        Assert.True(ContainerPreviewMachine.MapperGuardDone(true, windowStillOnTarget: false, 0));
        Assert.True(ContainerPreviewMachine.MapperGuardDone(worldAlive: false, true, 0));
        Assert.True(ContainerPreviewMachine.MapperGuardDone(true, true, ContainerPreviewMachine.MapperGuardSeconds));
    }

    // ---- wire ----------------------------------------------------------------------------------

    [Fact]
    public void The_event_id_is_in_Cores_reserved_range()
    {
        Assert.Equal(0x5072, (ContainerPreviewWire.EventId >> 16) & 0xFFFF);
        Assert.Equal("PrCp", Encoding.ASCII.GetString(BitConverter.GetBytes(ContainerPreviewWire.EventId)).ToCharArrayReversed());
    }

    [Fact]
    public void A_request_round_trips_and_never_decodes_as_an_answer()
    {
        var bytes = ContainerPreviewWire.Encode(new ContainerPreviewPacket
        { Kind = ContainerPreviewWire.KindOpen, Sequence = 7, Container = "c-" + new string('a', 32), Seconds = 30 });
        var p = ContainerPreviewWire.Decode(bytes, expectRequest: true);
        Assert.NotNull(p);
        Assert.Equal(7, p!.Sequence);
        Assert.Equal(30, p.Seconds);
        Assert.Null(ContainerPreviewWire.Decode(bytes, expectRequest: false));
    }

    [Fact]
    public void An_answer_round_trips_and_never_decodes_as_a_request()
    {
        var bytes = ContainerPreviewWire.Encode(new ContainerPreviewPacket
        { Kind = ContainerPreviewWire.KindReply, Sequence = 3, Ok = true, NetIndex = 1234, NetGeneration = 5, NetType = 1 });
        var p = ContainerPreviewWire.Decode(bytes, expectRequest: false);
        Assert.NotNull(p);
        Assert.True(p!.Ok);
        Assert.Equal(1234, p.NetIndex);
        Assert.Null(ContainerPreviewWire.Decode(bytes, expectRequest: true));
    }

    [Fact]
    public void Malformed_packets_decode_to_null()
    {
        Assert.Null(ContainerPreviewWire.Decode(Array.Empty<byte>(), true));
        Assert.Null(ContainerPreviewWire.Decode(Encoding.UTF8.GetBytes("not json"), true));
        Assert.Null(ContainerPreviewWire.Decode(new byte[ContainerPreviewWire.MaxBytes + 1], true));
        Assert.Null(ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = "open", Sequence = 0 }), true));
        Assert.Null(ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = "open", Sequence = 1, Version = 2 }), true));
        Assert.Null(ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = "steal", Sequence = 1 }), true));
        Assert.Null(ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(new ContainerPreviewPacket
            { Kind = "ended", Sequence = 1, Message = new string('x', ContainerPreviewWire.MaxText + 1) }), false));
        Assert.Equal(ContainerPreviewWire.MaxText, ContainerPreviewWire.Clip(new string('x', 999)).Length);
    }

    // ---- client state machine ------------------------------------------------------------------

    private static ContainerPreviewSample World(bool target = false, bool inventory = false, bool ours = false, bool other = false) => new()
    { SameWorld = true, TargetFound = target, InventoryReady = inventory, WindowIsOurs = ours, OtherMenuOpen = other };

    private static ContainerPreviewMachine Agreed(ContainerPreviewOptions? options = null, double now = 0)
    {
        var m = new ContainerPreviewMachine(options ?? new ContainerPreviewOptions(), now);
        Assert.True(m.OnReply(now, ok: true, ""));
        return m;
    }

    private static ContainerPreviewMachine OpenMachine(ContainerPreviewOptions? options = null)
    {
        var m = Agreed(options);
        Assert.Equal(ContainerPreviewStep.OpenWindow, m.Step(0.1, World(true, true)));
        Assert.Equal(ContainerPreviewStep.None, m.Step(0.2, World(true, true, ours: true)));
        Assert.Equal(ContainerPreviewState.Open, m.State);
        return m;
    }

    [Fact]
    public void The_happy_path_is_requesting_waiting_open()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions(), 0);
        Assert.Equal(ContainerPreviewState.Requesting, m.State);
        Assert.False(m.OwnsWindow);
        m.OnReply(0.5, true, "");
        Assert.Equal(ContainerPreviewState.Waiting, m.State);
        Assert.Equal(ContainerPreviewStep.None, m.Step(0.6, World(target: true)));            // inventory not here yet
        Assert.Equal(ContainerPreviewStep.OpenWindow, m.Step(0.7, World(true, true)));
        Assert.Equal(ContainerPreviewState.Waiting, m.State);                                  // Opening shows as Waiting
        Assert.True(m.OwnsWindow);
        Assert.Equal(ContainerPreviewStep.None, m.Step(0.8, World(true, true, ours: true)));
        Assert.Equal(ContainerPreviewState.Open, m.State);
    }

    [Fact]
    public void No_answer_closes_after_the_request_timeout()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions(), 0);
        Assert.Equal(ContainerPreviewStep.None, m.Step(ContainerPreviewMachine.RequestTimeoutSeconds - 0.1, default));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(ContainerPreviewMachine.RequestTimeoutSeconds, default));
        Assert.Equal(ContainerPreviewCloseReason.NoAnswer, m.CloseReason);
    }

    [Fact]
    public void A_refusal_closes_with_the_servers_sentence()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions(), 0);
        Assert.True(m.OnReply(1, false, "Only the castle's owner and clan can preview its containers."));
        Assert.Equal(ContainerPreviewState.Closed, m.State);
        Assert.Equal(ContainerPreviewCloseReason.Refused, m.CloseReason);
        Assert.Equal("Only the castle's owner and clan can preview its containers.", m.Message);
        Assert.False(m.OnReply(2, true, ""));   // a late answer changes nothing
    }

    [Fact]
    public void Replication_timeout_names_what_did_not_arrive()
    {
        var m = Agreed(new ContainerPreviewOptions { ReplicationTimeoutSeconds = 2 });
        Assert.Equal(ContainerPreviewStep.None, m.Step(1.9, World()));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(2.0, World()));
        Assert.Equal(ContainerPreviewCloseReason.ReplicationTimeout, m.CloseReason);
        Assert.Equal("The container did not arrive.", m.Message);

        m = Agreed(new ContainerPreviewOptions { ReplicationTimeoutSeconds = 2 });
        m.Step(2.0, World(target: true));
        Assert.Equal("The container's inventory did not arrive.", m.Message);
    }

    [Fact]
    public void Another_game_menu_blocks_the_open_until_it_closes_or_the_wait_ends()
    {
        var m = Agreed(new ContainerPreviewOptions { ReplicationTimeoutSeconds = 2 });
        Assert.Equal(ContainerPreviewStep.None, m.Step(1, World(true, true, other: true)));
        Assert.Equal(ContainerPreviewStep.OpenWindow, m.Step(1.5, World(true, true)));

        m = Agreed(new ContainerPreviewOptions { ReplicationTimeoutSeconds = 2 });
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(2, World(true, true, other: true)));
        Assert.Equal(ContainerPreviewCloseReason.OpenFailed, m.CloseReason);
    }

    [Fact]
    public void The_window_must_appear_within_the_open_timeout()
    {
        var m = Agreed();
        m.Step(0, World(true, true));
        Assert.Equal(ContainerPreviewStep.None, m.Step(ContainerPreviewMachine.OpenTimeoutSeconds - 0.1, World(true, true)));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(ContainerPreviewMachine.OpenTimeoutSeconds, World(true, true)));
        Assert.Equal(ContainerPreviewCloseReason.OpenFailed, m.CloseReason);
    }

    [Fact]
    public void An_open_preview_closes_when_the_game_closes_the_window()
    {
        var m = OpenMachine();
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(1, World(true, true, ours: false)));
        Assert.Equal(ContainerPreviewCloseReason.WindowClosed, m.CloseReason);
    }

    [Fact]
    public void An_open_preview_whose_target_leaves_holds_the_window_then_closes_after_the_grace()
    {
        var m = OpenMachine();
        var gone = new ContainerPreviewSample { SameWorld = true, ContainerMenuOpen = true };
        Assert.Equal(ContainerPreviewStep.None, m.Step(1, gone));
        Assert.True(m.InventoryMissing);
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(1 + ContainerPreviewMachine.InventoryGraceSeconds, gone));
        Assert.Equal(ContainerPreviewCloseReason.TargetGone, m.CloseReason);
        Assert.Equal("The container left this client.", m.Message);
    }

    [Fact]
    public void A_target_that_leaves_and_the_player_closing_the_window_meanwhile_is_window_closed()
    {
        var m = OpenMachine();
        m.Step(1, new ContainerPreviewSample { SameWorld = true, ContainerMenuOpen = true });
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(2, new ContainerPreviewSample { SameWorld = true, ContainerMenuOpen = false }));
        Assert.Equal(ContainerPreviewCloseReason.WindowClosed, m.CloseReason);
    }

    [Fact]
    public void A_target_that_comes_back_as_another_entity_ends_the_preview()
    {
        var m = OpenMachine();
        m.Step(1, new ContainerPreviewSample { SameWorld = true, ContainerMenuOpen = true });
        // Found again as another entity; the window is bound to the destroyed one: end at once, not after the grace.
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(2, new ContainerPreviewSample
            { SameWorld = true, TargetFound = true, TargetReplaced = true, InventoryReady = true, ContainerMenuOpen = true, WindowIsOurs = false }));
        Assert.Equal(ContainerPreviewCloseReason.TargetGone, m.CloseReason);
        Assert.Equal("The container left this client.", m.Message);
    }

    [Fact]
    public void A_replaced_target_ends_the_preview_even_while_its_inventory_is_still_missing()
    {
        var m = OpenMachine();
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(1, new ContainerPreviewSample
            { SameWorld = true, TargetFound = true, TargetReplaced = true, InventoryReady = false, ContainerMenuOpen = true }));
        Assert.Equal(ContainerPreviewCloseReason.TargetGone, m.CloseReason);
    }

    [Fact]
    public void The_same_chest_back_but_another_window_shown_is_window_closed()
    {
        var m = OpenMachine();
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(1, new ContainerPreviewSample
            { SameWorld = true, TargetFound = true, InventoryReady = true, ContainerMenuOpen = true, WindowIsOurs = false }));
        Assert.Equal(ContainerPreviewCloseReason.WindowClosed, m.CloseReason);
    }

    [Fact]
    public void The_client_flags_a_chest_that_came_back_as_another_entity()
    {
        string client = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewClient.cs");
        Assert.Contains("s.TargetReplaced = _windowTarget != Entity.Null && _target != Entity.Null && _target != _windowTarget;", client);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_world_change_closes_in_every_phase(int phaseValue)
    {
        var phase = (ContainerPreviewPhase)phaseValue;
        var m = Agreed();
        if (phase != ContainerPreviewPhase.Waiting) m.Step(0.1, World(true, true));
        if (phase == ContainerPreviewPhase.Open) m.Step(0.2, World(true, true, ours: true));
        Assert.Equal(phase, m.Phase);
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(0.3, new ContainerPreviewSample { SameWorld = false, TargetFound = true, WindowIsOurs = true }));
        Assert.Equal(ContainerPreviewCloseReason.WorldChanged, m.CloseReason);
    }

    [Fact]
    public void A_timed_preview_ends_at_its_seconds_counted_from_the_request()
    {
        var m = OpenMachine(new ContainerPreviewOptions { Seconds = 10 });
        Assert.Equal(ContainerPreviewStep.None, m.Step(9.9, World(true, true, ours: true)));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(10, World(true, true, ours: true)));
        Assert.Equal(ContainerPreviewCloseReason.Timeout, m.CloseReason);
        Assert.Equal(10, new ContainerPreviewMachine(new ContainerPreviewOptions { Seconds = 10 }, 0).RequestSeconds);
    }

    [Fact]
    public void Keep_open_has_no_deadline_and_renews_on_a_schedule()
    {
        var m = OpenMachine(new ContainerPreviewOptions { KeepOpen = true, Seconds = 1 });
        Assert.Equal(ContainerPreviewMachine.RenewGrantSeconds, new ContainerPreviewMachine(new ContainerPreviewOptions { KeepOpen = true }, 0).RequestSeconds);
        Assert.Equal(ContainerPreviewStep.None, m.Step(5000, World(true, true, ours: true)));
        Assert.False(m.RenewDue(ContainerPreviewMachine.RenewEverySeconds - 1));
        Assert.True(m.RenewDue(ContainerPreviewMachine.RenewEverySeconds));
        m.MarkRenewed(ContainerPreviewMachine.RenewEverySeconds);
        Assert.False(m.RenewDue(ContainerPreviewMachine.RenewEverySeconds + 1));
        Assert.True(ContainerPreviewMachine.RenewGrantSeconds >= 2 * ContainerPreviewMachine.RenewEverySeconds);
        m.Close(ContainerPreviewCloseReason.Disposed, "");
        Assert.False(m.RenewDue(1e9));
    }

    [Fact]
    public void A_timed_preview_never_renews()
    {
        var m = OpenMachine();
        Assert.False(m.RenewDue(1e9));
    }

    [Theory]
    [InlineData(ContainerPreviewCloseReason.Disposed)]
    [InlineData(ContainerPreviewCloseReason.Escape)]
    [InlineData(ContainerPreviewCloseReason.LeaseEnded)]
    [InlineData(ContainerPreviewCloseReason.Replaced)]
    [InlineData(ContainerPreviewCloseReason.Unloaded)]
    [InlineData(ContainerPreviewCloseReason.Failed)]
    public void Outside_closes_happen_once_and_stop_the_ticks(ContainerPreviewCloseReason reason)
    {
        var m = OpenMachine();
        Assert.True(m.Close(reason, "why"));
        Assert.False(m.Close(ContainerPreviewCloseReason.Disposed, "again"));
        Assert.Equal(reason, m.CloseReason);
        Assert.Equal("why", m.Message);
        Assert.False(m.OwnsWindow);
        Assert.Equal(ContainerPreviewStep.None, m.Step(1, World(true, true, ours: false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerPreviewMachine(new ContainerPreviewOptions(), 0).Close(ContainerPreviewCloseReason.None, ""));
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(601, 5, false)]
    [InlineData(double.NaN, 5, false)]
    [InlineData(30, 0, false)]
    [InlineData(30, 31, false)]
    [InlineData(600, 30, true)]
    public void Options_are_validated(double seconds, float replication, bool ok)
    {
        var o = new ContainerPreviewOptions { Seconds = seconds, ReplicationTimeoutSeconds = replication };
        Assert.Equal(ok, o.Problem() == null);
        if (!ok) Assert.Throws<ArgumentException>(() => new ContainerPreviewMachine(o, 0));
    }

    [Fact]
    public void Keep_open_ignores_seconds_and_the_defaults_hide_the_bag()
    {
        Assert.Null(new ContainerPreviewOptions { KeepOpen = true, Seconds = 0 }.Problem());
        var d = new ContainerPreviewOptions();
        Assert.True(d.HidePlayerInventory);
        Assert.True(d.DropItemEvents);
        Assert.False(d.KeepOpen);
        Assert.Equal(30, d.Seconds);
        Assert.Equal(5, d.ReplicationTimeoutSeconds);
    }

    // ---- HUD gate and Escape -------------------------------------------------------------------

    [Fact]
    public void The_hud_gate_skips_only_for_the_previews_own_window()
    {
        Assert.True(ContainerPreviewHudGate.ShouldSkip(true, false, true, true, true, 0));
        Assert.False(ContainerPreviewHudGate.ShouldSkip(false, false, true, true, true, 0));   // no preview owns it
        Assert.False(ContainerPreviewHudGate.ShouldSkip(true, false, false, true, true, 0));   // another menu
        Assert.False(ContainerPreviewHudGate.ShouldSkip(true, false, true, false, true, 0));   // another chest
        Assert.False(ContainerPreviewHudGate.ShouldSkip(true, false, true, true, false, 0));   // a real interaction
        Assert.False(ContainerPreviewHudGate.ShouldSkip(true, false, true, true, true, 1));    // the game wants a menu
        Assert.True(ContainerPreviewHudGate.ShouldSkip(true, true, true, false, true, 0));     // opening: target not bound yet
        Assert.False(ContainerPreviewHudGate.ShouldSkip(true, true, true, false, true, 1));
    }

    [Fact]
    public void Escape_goes_to_dialogs_captures_and_chat_before_the_preview()
    {
        Assert.Equal(ContainerPreviewEscapeOwner.NoPreview, ContainerPreviewEscape.Decide(false, true, true, true));
        Assert.Equal(ContainerPreviewEscapeOwner.Dialog, ContainerPreviewEscape.Decide(true, true, true, true));
        Assert.Equal(ContainerPreviewEscapeOwner.KeyCapture, ContainerPreviewEscape.Decide(true, false, true, true));
        Assert.Equal(ContainerPreviewEscapeOwner.Chat, ContainerPreviewEscape.Decide(true, false, false, true));
        Assert.Equal(ContainerPreviewEscapeOwner.Preview, ContainerPreviewEscape.Decide(true, false, false, false));
    }

    // ---- shared lease ---------------------------------------------------------------------------

    [Fact]
    public void A_second_holder_joins_at_the_same_point_and_is_refused_elsewhere()
    {
        Assert.Equal(ObservationJoin.Start, ObservationShareRules.Decide(false, false, false));
        Assert.Equal(ObservationJoin.Refuse, ObservationShareRules.Decide(false, false, true));
        Assert.Equal(ObservationJoin.Join, ObservationShareRules.Decide(true, true, false));
        Assert.Equal(ObservationJoin.Refuse, ObservationShareRules.Decide(true, false, false));
        Assert.True(ObservationShareRules.SamePoint(10, 0, 10, 10.3f, 0.2f, 10));
        Assert.False(ObservationShareRules.SamePoint(10, 0, 10, 10.6f, 0, 10));
        Assert.False(ObservationShareRules.SamePoint(float.NaN, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void The_lease_wide_safety_order_matches_the_camera_policy()
    {
        var fine = new CameraObservationSample { UserConnected = true, OriginOurs = true, CharacterRead = true };
        Assert.Equal(CameraObservationEndReason.None, ObservationShareRules.Safety(fine));
        Assert.Equal(CameraObservationEndReason.Disconnected, ObservationShareRules.Safety(default));
        var s = fine; s.OriginOurs = false; s.CharacterRead = false;
        Assert.Equal(CameraObservationEndReason.Superseded, ObservationShareRules.Safety(s));
        s = fine; s.CharacterRead = false;
        Assert.Equal(CameraObservationEndReason.CharacterGone, ObservationShareRules.Safety(s));
        s = fine; s.MovedMeters = float.NaN;
        Assert.Equal(CameraObservationEndReason.Failed, ObservationShareRules.Safety(s));
        s = fine; s.MovedMeters = CameraObservationPolicy.LeaveDistanceMeters + 0.1f;
        Assert.Equal(CameraObservationEndReason.LeftArea, ObservationShareRules.Safety(s));
        // The per-holder policy adds only its own expiry after the shared rules.
        var p = new CameraObservationPolicy(5, hold: false, now: 0);
        Assert.Equal(CameraObservationEndReason.Expired, p.Decide(5, fine));
        Assert.Equal(CameraObservationEndReason.LeftArea, p.Decide(5, s));
    }

    [Fact]
    public void Holders_keep_their_own_deadlines_and_the_lease_runs_until_the_last_one()
    {
        var book = new ObservationShareBook<string>();
        var camera = new CameraObservationPolicy(5, hold: false, now: 0);        // a timed camera preview
        var preview = new CameraObservationPolicy(10, hold: false, now: 0);
        preview.Renew(0, 30);                                                     // a 30 s container preview
        Assert.True(book.Add("camera", camera));
        Assert.True(book.Add("preview", preview));
        Assert.False(book.Add("camera", camera));
        Assert.Equal(5, book.NextDeadline);
        Assert.Equal(30, book.LastDeadline);

        Assert.Empty(book.TakeExpired(4.9));
        Assert.Equal(new[] { "camera" }, book.TakeExpired(5));
        Assert.Equal(1, book.Count);
        Assert.True(book.Contains("preview"));

        preview.Renew(20, 60);                                                    // a renewal moves only its own deadline
        Assert.Empty(book.TakeExpired(30));
        Assert.Equal(new[] { "preview" }, book.TakeExpired(80));
        Assert.Equal(0, book.Count);
        Assert.True(double.IsPositiveInfinity(book.NextDeadline));
    }

    [Fact]
    public void A_holder_leaving_keeps_the_others_and_a_lease_wide_end_takes_everyone()
    {
        var book = new ObservationShareBook<string>();
        book.Add("a", new CameraObservationPolicy(10, true, 0));
        book.Add("b", new CameraObservationPolicy(10, true, 0));
        book.Add("c", new CameraObservationPolicy(10, true, 0));
        Assert.True(book.Remove("b"));
        Assert.False(book.Remove("b"));
        Assert.Equal(2, book.Count);
        Assert.Equal(new[] { "a", "c" }, book.TakeAll());
        Assert.Equal(0, book.Count);
    }
}

internal static class StringReverseExtensions
{
    internal static string ToCharArrayReversed(this string s)
    {
        var a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }
}
