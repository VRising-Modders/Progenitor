using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Castles;
using Progenitor.Chat;
using Progenitor.Dev.Ops;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The servant hunt success chance (<see cref="ServantChanceLogic"/>) against the game's formula,
/// the <c>[mission:guid]</c> chat token, and the bridge's <c>chance</c> action. Every id, GUID and
/// number is a placeholder.
/// </summary>
public class ServantChanceTests
{
    private const int PerkA = 1001, PerkB = 1002, PerkC = 1003;

    private static ChanceEntry Entry(int index, float proficiency, int perk0 = 0, int perk1 = 0, bool valid = true) => new()
    {
        Servant = new CastleId(index, 1),
        Proficiency = proficiency,
        Perk0 = perk0,
        Perk1 = perk1,
        Valid = valid,
    };

    private static ChanceMission Mission(int difficulty, int partyBonus = 0, float bonus = 0f, params int[] perks) => new()
    {
        Difficulty = difficulty,
        StaticPartyBonus = partyBonus,
        SuccessRateBonus = bonus,
        Perks = perks,
        PerkData = guid => guid switch
        {
            PerkA => (0.25f, 30),
            PerkB => (0.5f, 20),
            _ => null,
        },
    };

    /// <summary>The game's last step, written out independently: float sum, double square, float cast, clamp.</summary>
    private static float Reference(float proficiency, int party, int difficulty, int reduced, float bonus)
    {
        float x = proficiency + party - (float)(difficulty - reduced);
        if (x >= 0) return 1f;
        float v = (float)(1.0 - (double)x * x / 15000.0) + bonus;
        return Math.Clamp(v, 0f, 1f);
    }

    [Fact]
    public void A_short_lone_servant_loses_the_shortfall_squared_over_15000()
    {
        var c = ServantChanceLogic.Compute(Mission(250), new[] { Entry(1, 200) }, 0);
        Assert.Equal(1f - 2500f / 15000f, c.Vanilla, 5);
        Assert.Equal(Reference(200, 0, 250, 0, 0), c.Vanilla);
        Assert.Equal(200, c.Proficiency);
        Assert.Equal(0, c.PartyBonus);
        Assert.Equal(250, c.EffectiveDifficulty);
        Assert.Equal(1f, c.PerkLootBonus);
        Assert.Null(c.MaxSuccess);
        Assert.Equal(c.Vanilla, c.Applied);
    }

    [Theory]
    [InlineData(250f, 250)]
    [InlineData(300f, 250)]
    public void At_or_above_the_difficulty_is_certain_whatever_the_length_bonus(float proficiency, int difficulty)
    {
        Assert.Equal(1f, ServantChanceLogic.Compute(Mission(difficulty, bonus: -0.5f), new[] { Entry(1, proficiency) }, 0).Vanilla);
    }

    [Fact]
    public void The_length_bonus_counts_only_when_short_and_the_result_is_clamped()
    {
        Assert.Equal(Reference(200, 0, 250, 0, 0.1f), ServantChanceLogic.Compute(Mission(250, bonus: 0.1f), new[] { Entry(1, 200) }, 0).Vanilla);
        Assert.Equal(1f, ServantChanceLogic.Compute(Mission(250, bonus: 0.5f), new[] { Entry(1, 200) }, 0).Vanilla);
        // x = -200: 1 - 40000/15000 < 0.
        Assert.Equal(0f, ServantChanceLogic.Compute(Mission(400, bonus: 0.2f), new[] { Entry(1, 200) }, 0).Vanilla);
    }

    [Fact]
    public void Each_servant_gets_the_party_bonus_once_per_other_servant_and_its_own_proficiency()
    {
        var party = new[] { Entry(1, 180), Entry(2, 200), Entry(3, 220) };
        var all = ServantChanceLogic.ComputeAll(Mission(300, partyBonus: 15), party);
        Assert.Equal(3, all.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(30, all[i].PartyBonus);
            Assert.Equal(Reference(party[i].Proficiency, 30, 300, 0, 0), all[i].Vanilla);
        }
        Assert.True(all[0].Vanilla < all[1].Vanilla && all[1].Vanilla < all[2].Vanilla);
    }

    [Fact]
    public void A_mission_perk_counts_once_per_party_whoever_has_it()
    {
        // Servant 1 has A first; servant 2 has A second and B first; servant 3 has an unrelated C.
        var party = new[] { Entry(1, 200, PerkA), Entry(2, 200, PerkB, PerkA), Entry(3, 200, PerkC) };
        var c = ServantChanceLogic.Compute(Mission(400, partyBonus: 10, 0f, PerkA, PerkB), party, 0);
        Assert.Equal(50, c.PerkReduction);
        Assert.Equal(350, c.EffectiveDifficulty);
        Assert.Equal(1f + 0.25f + 0.5f, c.PerkLootBonus, 5);
        Assert.Equal(Reference(200, 20, 400, 50, 0), c.Vanilla);
    }

    [Fact]
    public void A_perk_without_perk_data_is_used_up_but_adds_nothing()
    {
        var c = ServantChanceLogic.Compute(Mission(260, 0, 0f, PerkC), new[] { Entry(1, 200, PerkC), Entry(2, 200, PerkC) }, 0);
        Assert.Equal(0, c.PerkReduction);
        Assert.Equal(1f, c.PerkLootBonus);
    }

    [Fact]
    public void An_invalid_entry_counts_for_nothing_and_gets_no_chance()
    {
        var party = new[] { Entry(1, 200), Entry(2, 900, PerkA, valid: false) };
        var c = ServantChanceLogic.Compute(Mission(250, partyBonus: 25, 0f, PerkA), party, 0);
        Assert.Equal(0, c.PartyBonus);
        Assert.Equal(0, c.PerkReduction);
        Assert.Single(ServantChanceLogic.ComputeAll(Mission(250), party));
    }

    [Fact]
    public void A_cap_only_fills_max_and_applied()
    {
        var c = ServantChanceLogic.Compute(Mission(100), new[] { Entry(1, 200) }, 0, cap: 0.75f);
        Assert.Equal(1f, c.Vanilla);
        Assert.Equal(0.75f, c.MaxSuccess);
        Assert.Equal(0.75f, c.Applied);
        var low = ServantChanceLogic.Compute(Mission(250), new[] { Entry(1, 200) }, 0, cap: 0.9f);
        Assert.Equal(low.Vanilla, low.Applied);
    }

    [Fact]
    public void With_roll_attaches_the_row_servants_chance()
    {
        var returned = new ReturnedServant { Servant = new CastleId(5, 1), Name = "A", LootIsHaul = true };
        var chances = new List<ServantChance> { new() { Servant = new CastleId(4, 1), Vanilla = 0.5f }, new() { Servant = new CastleId(5, 1), Vanilla = 0.8f } };
        var with = ServantChanceLogic.WithRoll(returned, new CastleId(5, 1), chances);
        Assert.Equal(0.8f, with.Chance!.Vanilla);
        Assert.True(with.LootIsHaul);
        Assert.Same(returned, ServantChanceLogic.WithRoll(returned, new CastleId(9, 1), chances));
    }

    // ---- the [mission:guid] chat token ------------------------------------------------------------

    [Fact]
    public void Mission_tokens_are_found_strictly()
    {
        var found = ChatTokens.Find("hunt [mission:-1206352345] then [mission:0] [mission:x] [mission:12345678901234] [item:7]");
        Assert.Equal(2, found.Count);
        Assert.Equal(ChatSpanKind.Mission, found[0].Kind);
        Assert.Equal("-1206352345", found[0].Target);
        Assert.Equal(ChatSpanKind.Item, found[1].Kind);
        Assert.Equal("[mission:-42]", ChatTokens.Mission(-42));
    }

    [Fact]
    public void Mission_tokens_render_as_the_plain_name_or_as_written()
    {
        var old = ChatFormat.MissionNameResolver;
        try
        {
            ChatFormat.MissionNameResolver = id => id == 42 ? "Fishing Lake" : null;
            var r = new ChatRecord { Kind = ChatKind.System, Text = "Hunt in [mission:42] started. [mission:7]" };
            string line = ChatFormat.Render(r, new ChatFormat.Options(), out var spans);
            Assert.Equal(2, spans.Count);
            Assert.Contains("Fishing Lake", line);
            Assert.DoesNotContain("[Fishing Lake]", line);
            Assert.Contains("[mission:7]", line);
            Assert.Equal(ChatSpanKind.Mission, ChatFormat.SpanForLink("h:0", spans)!.Value.Kind);
            Assert.Null(ChatFormat.SpanForLink("i:0", spans));
        }
        finally { ChatFormat.MissionNameResolver = old; }
    }

    // ---- the bridge's chance action ---------------------------------------------------------------

    [Fact]
    public void The_bridge_chance_action_takes_the_send_arguments()
    {
        var r = ServantArgs.Parse(JsonDocument.Parse("{\"action\":\"chance\",\"territory\":3,\"servants\":[\"#1\",\"#2\"],\"mission\":-5,\"slot\":2}").RootElement);
        Assert.Equal(ServantArgs.Action.Chance, r.Action);
        Assert.Equal(3, r.Territory);
        Assert.Equal(-5, r.Mission);
        Assert.Equal(2, r.Slot);
        Assert.Equal(2, r.Servants!.Count);
        Assert.Contains("chance", ServantArgs.Actions);
    }
}
