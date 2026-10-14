using System.Collections.Generic;
using Progenitor.Players;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Players.State</c>: the mapping from what the live reader collects
/// (health, the <c>Dead</c> component, buffs on the character, the controller's shapeshift flag)
/// to <see cref="PlayerStateInfo"/>, and the form-buff classification.
/// </summary>
public class PlayerStateLogicTests
{
    const int Downed = PlayerStateLogic.DownedBuffGuid;
    const int Pvp = PlayerStateLogic.PvpCombatBuffGuid;
    const int Combat = PlayerStateLogic.CombatBuffGuid;

    static PlayerStateFacts Alive(params (int, string)[] buffs) => new()
    {
        Online = true,
        HasCharacter = true,
        CharacterExists = true,
        HealthRead = true,
        Health = 120f,
        MaxHealth = 150f,
        BuffsRead = true,
        Buffs = new List<(int, string)>(buffs),
        DownedBuff = Downed,
        PvpCombatBuff = Pvp,
        CombatBuff = Combat,
    };

    [Theory]
    [InlineData("AB_Shapeshift_Bat_TakeFlight_Buff", PlayerShapeshiftKind.Bat)]
    [InlineData("AB_Shapeshift_Wolf_Buff", PlayerShapeshiftKind.Wolf)]
    [InlineData("AB_Shapeshift_Wolf_PMK_Skin02_Buff", PlayerShapeshiftKind.Wolf)]
    [InlineData("AB_Shapeshift_Bear_Skin01_Buff", PlayerShapeshiftKind.Bear)]
    [InlineData("AB_Shapeshift_Rat_Buff", PlayerShapeshiftKind.Rat)]
    [InlineData("AB_Shapeshift_Toad_Buff", PlayerShapeshiftKind.Toad)]
    [InlineData("AB_Shapeshift_Human_Grandma_Skin01_Buff", PlayerShapeshiftKind.Human)]
    public void Named_form_buffs_map_to_their_kind(string name, PlayerShapeshiftKind kind)
    {
        Assert.Equal(PlayerStateLogic.FormBuff.Known, PlayerStateLogic.Classify(name, out var k));
        Assert.Equal(kind, k);
    }

    [Theory]
    [InlineData("AB_Shapeshift_BloodMend_Buff")]
    [InlineData("AB_Shapeshift_BloodHunger_BloodSight_Buff")]
    [InlineData("AB_Shapeshift_ShareBlood_ExposeVein_Buff")]
    [InlineData("AB_Shapeshift_NormalForm_Buff")]
    [InlineData("AB_Shapeshift_DominatingPresence_PsychicForm_Buff")]
    [InlineData("AB_Shapeshift_CommandingPresence_Buff")]
    public void Vampire_powers_from_the_form_menu_are_not_forms(string name)
    {
        Assert.Equal(PlayerStateLogic.FormBuff.Power, PlayerStateLogic.Classify(name, out var k));
        Assert.Equal(PlayerShapeshiftKind.None, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Buff_InCombat")]
    [InlineData("AB_Shapeshift_Bat_Group")]          // an ability group, not a buff
    [InlineData("ab_shapeshift_bat_buff")]           // names are case-sensitive
    public void Other_names_are_not_shapeshift_buffs(string? name)
    {
        Assert.Equal(PlayerStateLogic.FormBuff.NotShapeshift, PlayerStateLogic.Classify(name, out _));
    }

    [Fact]
    public void Unnamed_forms_are_other()
    {
        Assert.Equal(PlayerStateLogic.FormBuff.Unknown, PlayerStateLogic.Classify("AB_Shapeshift_Spider_Buff", out var k));
        Assert.Equal(PlayerShapeshiftKind.Other, k);
    }

    [Fact]
    public void A_plain_living_player_is_standing_with_no_flags()
    {
        var s = PlayerStateLogic.Build(Alive());
        Assert.True(s.Ok);
        Assert.True(s.Standing);
        Assert.False(s.Dead);
        Assert.False(s.Downed);
        Assert.False(s.InPvpCombat);
        Assert.False(s.InCombat);
        Assert.False(s.Shapeshifted);
        Assert.Equal(PlayerShapeshiftKind.None, s.ShapeshiftKind);
        Assert.Equal(120f, s.Health);
        Assert.Equal("", s.Reason);
        Assert.StartsWith("online standing form=None", s.ToString());
    }

    [Fact]
    public void Dead_comes_from_health_or_the_dead_component_and_hides_downed()
    {
        var f = Alive((Downed, "Buff_General_Vampire_Wounded_Buff"));
        Assert.True(PlayerStateLogic.Build(f).Downed);

        f.HealthIsDead = true;
        var byHealth = PlayerStateLogic.Build(f);
        Assert.True(byHealth.Dead);
        Assert.False(byHealth.Downed);
        Assert.False(byHealth.Standing);

        f.HealthIsDead = false;
        f.HasDeadComponent = true;
        Assert.True(PlayerStateLogic.Build(f).Dead);
    }

    [Fact]
    public void Pvp_combat_is_also_combat_and_pve_combat_is_not_pvp()
    {
        var pvp = PlayerStateLogic.Build(Alive((Pvp, "Buff_InCombat_PvPVampire")));
        Assert.True(pvp.InPvpCombat);
        Assert.True(pvp.InCombat);

        var pve = PlayerStateLogic.Build(Alive((Combat, "Buff_InCombat")));
        Assert.False(pve.InPvpCombat);
        Assert.True(pve.InCombat);
    }

    [Fact]
    public void The_buff_guids_come_from_the_facts_not_the_recorded_constants()
    {
        // A game update renumbers a prefab; the reader resolves it by name and passes the live GUID.
        var f = Alive((424242, "Buff_InCombat_PvPVampire"));
        f.PvpCombatBuff = 424242;
        Assert.True(PlayerStateLogic.Build(f).InPvpCombat);
        f.PvpCombatBuff = Pvp;
        Assert.False(PlayerStateLogic.Build(f).InPvpCombat);
    }

    [Fact]
    public void A_named_form_buff_decides_the_kind()
    {
        var bat = PlayerStateLogic.Build(Alive((100, "AB_Shapeshift_BloodMend_Buff"), (576, "AB_Shapeshift_Bat_TakeFlight_Buff")));
        Assert.True(bat.Shapeshifted);
        Assert.Equal(PlayerShapeshiftKind.Bat, bat.ShapeshiftKind);
        Assert.Equal(576, bat.ShapeshiftBuff);
        Assert.Equal("AB_Shapeshift_Bat_TakeFlight_Buff", bat.ShapeshiftBuffName);

        // Even when the controller flag reads false (the flag and the buff can disagree for a frame).
        var f = Alive((7, "AB_Shapeshift_Wolf_Buff"));
        f.ControllerShapeshifted = false;
        Assert.Equal(PlayerShapeshiftKind.Wolf, PlayerStateLogic.Build(f).ShapeshiftKind);
    }

    [Fact]
    public void Controller_flag_or_an_unnamed_form_buff_is_other()
    {
        var flag = Alive();
        flag.ControllerShapeshifted = true;
        var byFlag = PlayerStateLogic.Build(flag);
        Assert.Equal(PlayerShapeshiftKind.Other, byFlag.ShapeshiftKind);
        Assert.Equal(0, byFlag.ShapeshiftBuff);

        var unknown = Alive((9, "AB_Shapeshift_Spider_Buff"));
        Assert.Equal(PlayerShapeshiftKind.Other, PlayerStateLogic.Build(unknown).ShapeshiftKind);
        unknown.ControllerShapeshifted = false; // the controller says no: not a form
        Assert.Equal(PlayerShapeshiftKind.None, PlayerStateLogic.Build(unknown).ShapeshiftKind);

        var power = Alive((100, "AB_Shapeshift_BloodMend_Buff"));
        Assert.Equal(PlayerShapeshiftKind.None, PlayerStateLogic.Build(power).ShapeshiftKind);
    }

    [Fact]
    public void Offline_and_disabled_characters_are_read_and_say_so()
    {
        var f = Alive((Pvp, "Buff_InCombat_PvPVampire"));
        f.Online = false;
        f.CharacterDisabled = true;
        var s = PlayerStateLogic.Build(f);
        Assert.True(s.Ok);
        Assert.False(s.Online);
        Assert.True(s.CharacterDisabled);
        Assert.True(s.InPvpCombat);
        Assert.StartsWith("offline disabled", s.ToString());
    }

    [Fact]
    public void Missing_character_gives_no_character_and_every_flag_false()
    {
        var none = PlayerStateLogic.Build(new PlayerStateFacts { Online = true });
        Assert.Equal(PlayerStateStatus.NoCharacter, none.Status);
        Assert.False(none.Ok);
        Assert.True(none.Online);
        Assert.False(none.Standing);
        Assert.Contains("no character", none.Reason);

        var gone = PlayerStateLogic.Build(new PlayerStateFacts { HasCharacter = true, CharacterExists = false });
        Assert.Equal(PlayerStateStatus.NoCharacter, gone.Status);
        Assert.Contains("no longer exists", gone.Reason);
        Assert.Equal("NoCharacter (the user's character entity no longer exists)", gone.ToString());
    }

    [Fact]
    public void Unreadable_health_or_buffs_are_noted_not_thrown()
    {
        var f = Alive();
        f.HealthRead = false;
        f.Health = 99f;
        f.BuffsRead = false;
        f.Buffs = null;
        var s = PlayerStateLogic.Build(f);
        Assert.True(s.Ok);
        Assert.Equal(0f, s.Health);
        Assert.Contains("health unreadable", s.Reason);
        Assert.Contains("buffs unreadable", s.Reason);
    }

    [Fact]
    public void Fail_results_carry_the_status_and_reason()
    {
        var stale = PlayerStateInfo.Fail(PlayerStateStatus.Stale, "from an earlier world load");
        Assert.False(stale.Ok);
        Assert.False(stale.Dead || stale.Downed || stale.InPvpCombat || stale.Shapeshifted);
        Assert.Equal("Stale (from an earlier world load)", stale.ToString());
    }

    [Fact]
    public void Sample_players_state_parses_with_or_without_a_name()
    {
        Assert.True(SamplePlayerDirectory.TryParse("state", "", out var self, out string none, out _));
        Assert.Equal(SamplePlayerDirectory.Kind.State, self);
        Assert.Equal("", none);
        Assert.True(SamplePlayerDirectory.TryParse("STATE", "Ada", out var byName, out string name, out _));
        Assert.Equal(SamplePlayerDirectory.Kind.State, byName);
        Assert.Equal("Ada", name);
        Assert.Equal("State of you: online standing", SamplePlayerDirectory.StateReply("you", "online standing"));
        Assert.Contains(".players state", SamplePlayerDirectory.UsageReply);
    }
}
