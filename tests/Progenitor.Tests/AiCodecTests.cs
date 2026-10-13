using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Ai;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Ai's game-free half against bytes read from a live Frostmaw (CHAR_Wendigo_VBlood) on the
/// server, 2026-09-24 (a raw capture,
/// trimmed into Fixtures/wendigo-ai.json). The expected values are the ones the research decoder and
/// live tests T1-T6 established.
/// </summary>
public class AiCodecTests
{
    private sealed class Capture
    {
        public int Boss;
        public byte[] Tree = Array.Empty<byte>();
        public List<List<(byte[] Raw, byte[]? Condition)>> Groups = new();
        public List<AiAbilitySlot> Slots = new();
        public byte[] Phase = Array.Empty<byte>();
    }

    private static readonly Lazy<Capture> Wendigo = new(Load);

    private static readonly string[] SlotNames =
    {
        "AB_Wendigo_MeleeAttack_AbilityGroup",
        "AB_Wendigo_IcicleThrow_AbilityGroup",
        "AB_Wendigo_FrostNova_AbilityGroup",
        "AB_Wendigo_IceDash_Travel_AbilityGroup",
        "AB_Wendigo_IceDash_Strike_AbilityGroup",
        "AB_Wendigo_IceBeam_First_AbilityGroup",
        "AB_Wendigo_SnowStorm_AbilityGroup",
        "AB_Wendigo_IceDash_Init_AbilityGroup",
        "AB_Wendigo_IceBeam_Second_AbilityGroup",
        "AB_Wendigo_IceDash_Travel_Hard_AbilityGroup",
        "AB_Wendigo_IceDash_Strike_Hard_AbilityGroup",
        "AB_Wendigo_LeapAttack_AbilityGroup",
    };

    private static Capture Load()
    {
        string path = Path.Combine(SourcePin.RepoRoot(), "tests", "Progenitor.Tests", "Fixtures", "wendigo-ai.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var capture = new Capture
        {
            Boss = root.GetProperty("boss").GetInt32(),
            Tree = Convert.FromBase64String(root.GetProperty("tree").GetProperty("data").GetString()!),
            Phase = Convert.FromBase64String(root.GetProperty("phaseScript").GetString()!),
        };
        int slot = 0;
        foreach (var guid in root.GetProperty("abilitySlots").EnumerateArray())
        {
            capture.Slots.Add(new AiAbilitySlot(slot, guid.GetInt32(), SlotNames[slot]));
            slot++;
        }
        foreach (var group in root.GetProperty("castOptions").GetProperty("groups").EnumerateArray())
        {
            var rows = new List<(byte[], byte[]?)>();
            foreach (var row in group.GetProperty("rows").EnumerateArray())
            {
                var condition = row.GetProperty("condition");
                rows.Add((Convert.FromBase64String(row.GetProperty("raw").GetString()!),
                    condition.ValueKind == JsonValueKind.Null ? null : Convert.FromBase64String(condition.GetString()!)));
            }
            capture.Groups.Add(rows);
        }
        return capture;
    }

    private static List<AiCastOptionGroup> Decoded()
    {
        var groups = new List<AiCastOptionGroup>();
        var c = Wendigo.Value;
        for (int g = 0; g < c.Groups.Count; g++)
        {
            var rows = new List<AiCastOptionRow>();
            for (int r = 0; r < c.Groups[g].Count; r++)
            {
                var row = CastOptionCodec.Decode(c.Groups[g][r].Raw, g, r, c.Slots);
                if (c.Groups[g][r].Condition is byte[] condition)
                {
                    var gates = ConditionCodec.HealthGates(condition);
                    if (gates.Count > 0) row.HealthGate = ConditionCodec.ReadGate(condition, gates[0]);
                }
                rows.Add(row);
            }
            groups.Add(new AiCastOptionGroup(262152, g, rows));
        }
        return groups;
    }

    // ---- layout ------------------------------------------------------------------------------------

    [Fact]
    public void Strides_derived_from_the_declared_layout_match_the_captured_memory()
    {
        Assert.Equal(0x78, CastOptionCodec.Stride);
        Assert.Equal(0x28, ConditionCodec.Stride);
        Assert.Equal(0x18, TreeBlobReader.NodeStride);
        Assert.All(Wendigo.Value.Groups.SelectMany(g => g), row => Assert.Equal(CastOptionCodec.Stride, row.Raw.Length));
    }

    [Fact]
    public void Public_enums_carry_the_game_values_the_dump_declares()
    {
        Assert.Equal(Layout.CastOptionAggroFilterNone, (int)CastAggroFilter.None);
        Assert.Equal(Layout.CastOptionLineOfSightNone, (int)CastLineOfSight.None);
        Assert.Equal(Layout.CastOptionAngleCounterClockSide180, (int)CastAngle.CounterClockSide180);
        Assert.Equal(13, Enum.GetValues<CastAngle>().Length);
    }

    // ---- cast options ------------------------------------------------------------------------------

    [Fact]
    public void Frostmaw_rows_decode_to_the_researched_table()
    {
        var groups = Decoded();
        Assert.Equal(new[] { 9, 7, 1 }, groups.Select(g => g.Rows.Count).ToArray());

        var melee = groups[0].Rows[0];
        Assert.Equal("AB_Wendigo_MeleeAttack_AbilityGroup", melee.AbilityGroupName);
        Assert.Equal(33, melee.Weight);
        Assert.Equal(7f, melee.RangeMax);
        Assert.Equal(CastAggroFilter.PrimaryAggroTargetOnly, melee.AggroFilter);
        Assert.Equal(CastLineOfSight.RequireLineOfSight, melee.LineOfSight);
        Assert.True(melee.HasCondition);

        var nova = groups[1].Rows[2];
        Assert.Equal(2, nova.AbilitySlot);
        Assert.Equal("AB_Wendigo_FrostNova_AbilityGroup", nova.AbilityGroupName);
        Assert.Equal(50, nova.Weight);
        Assert.Equal(-1f, nova.CooldownMin);
        Assert.Equal(25f, nova.CooldownMax);
        Assert.Equal("exactly 25 s", nova.CooldownText);
        Assert.Equal(6f, nova.RangeMax);
        Assert.True(nova.HasGroupCondition);

        var icicleFollowUp = groups[0].Rows[1];
        Assert.Equal(1, icicleFollowUp.IgnoreCooldownFlags);
        Assert.Equal(0.5f, icicleFollowUp.CooldownMax);
        Assert.Equal(305, groups[1].Rows.Sum(r => r.Weight));
    }

    [Fact]
    public void Health_gates_match_the_decoded_conditions_and_live_T2()
    {
        var gates = Decoded()[1].Rows.ToDictionary(r => AiNames.Short(r.AbilityGroupName), r => r.HealthGate);
        Assert.Equal(0.8f, gates["IceDash_Init"]!.Value, 3);
        Assert.Equal(0.5f, gates["IceBeam_First"]!.Value, 3);
        Assert.Equal(0.6f, gates["SnowStorm"]!.Value, 3);
        Assert.Equal(0.7f, gates["LeapAttack"]!.Value, 3);
        Assert.Null(gates["FrostNova"]);
    }

    [Fact]
    public void An_ability_group_is_resolved_by_short_name_full_name_or_guid_never_by_row()
    {
        var slots = Wendigo.Value.Slots;
        Assert.True(CastOptionCodec.TryResolveGroup(slots, "FrostNova", out int byShort, out _));
        Assert.True(CastOptionCodec.TryResolveGroup(slots, "ab_wendigo_frostnova_abilitygroup", out int byName, out _));
        Assert.True(CastOptionCodec.TryResolveGroup(slots, "1238313774", out int byGuid, out _));
        Assert.Equal(1238313774, byShort);
        Assert.Equal(byShort, byName);
        Assert.Equal(byShort, byGuid);
        Assert.True(CastOptionCodec.TryResolveGroup(slots, "IceDash_Travel", out int travel, out _));
        Assert.Equal(-496233120, travel);

        Assert.False(CastOptionCodec.TryResolveGroup(slots, "Fireball", out _, out string error));
        Assert.Contains("FrostNova", error);
        Assert.False(CastOptionCodec.TryResolveGroup(slots, "12345", out _, out _));
    }

    [Fact]
    public void Selecting_a_group_takes_every_row_that_casts_it()
    {
        var groups = Decoded();
        int icicle = Wendigo.Value.Slots[1].GroupGuid;
        Assert.Equal(3, CastOptionCodec.Select(groups, icicle).Count);
        Assert.Single(CastOptionCodec.Select(groups, icicle, priority: 1));
        Assert.Empty(CastOptionCodec.Select(groups, 42));
    }

    [Fact]
    public void An_edit_writes_only_its_fields_and_restores_the_exact_bytes()
    {
        byte[] original = Wendigo.Value.Groups[1][2].Raw;
        byte[] row = (byte[])original.Clone();
        var patches = CastOptionCodec.Apply(row, new CastOptionEdit { Weight = 500, CooldownSeconds = 2f, LineOfSight = CastLineOfSight.None });

        var edited = CastOptionCodec.Decode(row, 1, 2, Wendigo.Value.Slots);
        Assert.Equal(500, edited.Weight);
        Assert.Equal(-1f, edited.CooldownMin);
        Assert.Equal(2f, edited.CooldownMax);
        Assert.Equal(CastLineOfSight.None, edited.LineOfSight);
        Assert.Equal(6f, edited.RangeMax);

        int changed = Enumerable.Range(0, row.Length).Count(i => row[i] != original[i]);
        Assert.InRange(changed, 1, 12);

        for (int i = patches.Count - 1; i >= 0; i--) Assert.True(patches[i].Restore(row));
        Assert.Equal(original, row);
    }

    [Fact]
    public void Restore_leaves_a_field_someone_changed_again()
    {
        byte[] row = (byte[])Wendigo.Value.Groups[1][2].Raw.Clone();
        var patches = CastOptionCodec.Apply(row, new CastOptionEdit { Weight = 500 });
        CastOptionCodec.Apply(row, new CastOptionEdit { Weight = 7 });
        Assert.False(patches[0].Restore(row));
        Assert.Equal(7, CastOptionCodec.Decode(row, 1, 2, Wendigo.Value.Slots).Weight);
    }

    [Fact]
    public void An_invalid_edit_is_refused_before_any_byte_moves()
    {
        byte[] row = (byte[])Wendigo.Value.Groups[1][2].Raw.Clone();
        Assert.Throws<ArgumentException>(() => CastOptionCodec.Apply(row, new CastOptionEdit()));
        Assert.Throws<ArgumentException>(() => CastOptionCodec.Apply(row, new CastOptionEdit { CooldownSeconds = -1f }));
        Assert.Throws<ArgumentException>(() => CastOptionCodec.Apply(row, new CastOptionEdit { RangeMin = 5f, RangeMax = 2f }));
        Assert.Throws<ArgumentException>(() => CastOptionCodec.Apply(row, new CastOptionEdit { Weight = -3 }));
        Assert.Equal(Wendigo.Value.Groups[1][2].Raw, row);
    }

    // ---- conditions ---------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_health_gate_changes_only_the_gate_value()
    {
        byte[] original = Wendigo.Value.Groups[1][5].Condition!;
        byte[] copy = (byte[])original.Clone();
        Assert.Equal(1, ConditionCodec.WriteGates(copy, 1f));
        int gate = ConditionCodec.HealthGates(copy).Single();
        Assert.Equal(1f, ConditionCodec.ReadGate(copy, gate));
        Assert.Equal(4, ConditionCodec.ReadGateCompare(copy, gate));
        int changed = Enumerable.Range(0, copy.Length).Count(i => copy[i] != original[i]);
        Assert.InRange(changed, 1, 4);
    }

    [Fact]
    public void A_row_without_a_boss_health_gate_has_nothing_to_move()
    {
        byte[] melee = (byte[])Wendigo.Value.Groups[0][0].Condition!;
        Assert.Empty(ConditionCodec.HealthGates(melee));
        Assert.Equal(0, ConditionCodec.WriteGates(melee, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionCodec.WriteGates((byte[])melee.Clone(), 1.5f));
    }

    [Fact]
    public void The_condition_summary_names_the_gate_and_the_sources()
    {
        string snow = ConditionCodec.Summarize(Wendigo.Value.Groups[1][5].Condition!);
        Assert.StartsWith("HealthFactor(Owner) <= 0.6", snow);
        Assert.Contains("IsAlly", snow);
        Assert.Contains("elements)", snow);
        string melee = ConditionCodec.Summarize(Wendigo.Value.Groups[0][0].Condition!);
        Assert.Contains("LastAbilityCasted", melee);
        Assert.Contains("27 elements", melee);
    }

    // ---- behavior tree -----------------------------------------------------------------------------

    private static TreeVariant Tree() =>
        new("CHAR_Wendigo_VBlood", Wendigo.Value.Boss, "BEH_Wendigo_VBlood", -646154887, (byte[])Wendigo.Value.Tree.Clone());

    [Fact]
    public void The_tree_blob_parses_to_the_researched_shape()
    {
        var tree = Tree();
        Assert.Equal(421, tree.NodeCount);
        Assert.Equal("Root", tree.NodeType(0));
        Assert.Equal(new[] { 420 }, tree.FindNodes("AttackEntityCastOption"));
        Assert.Equal(20, tree.FindNodes("Delay").Count);
        Assert.Equal(76, TreeBlobReader.TypeHistogram(Wendigo.Value.Tree).Count);
        Assert.Equal("Sequence", TreeBlobReader.TypeHistogram(Wendigo.Value.Tree)[0].Key);
    }

    [Fact]
    public void Disabling_combat_turns_the_cast_node_into_Succeed_in_the_copy_only()
    {
        var tree = Tree();
        int succeedBefore = tree.FindNodes("Succeed").Count;
        Assert.Equal(1, tree.DisableCombat());
        Assert.Empty(tree.FindNodes("AttackEntityCastOption"));
        Assert.Equal(succeedBefore + 1, tree.FindNodes("Succeed").Count);
        Assert.True(TreeBlobReader.TryNodes(tree.Data, out int first, out _));
        int typeField = first + 420 * TreeBlobReader.NodeStride + Layout.BehaviourTreeNodeTypeOffset;
        Assert.All(
            Enumerable.Range(0, tree.Data.Length).Where(i => tree.Data[i] != Wendigo.Value.Tree[i]),
            i => Assert.InRange(i, typeField, typeField + 3));
        Assert.Single(new TreeVariant("b", 1, "t", 2, (byte[])Wendigo.Value.Tree.Clone()).FindNodes("AttackEntityCastOption"));
    }

    [Fact]
    public void Node_parameters_are_written_only_on_their_own_node_type()
    {
        var tree = Tree();
        int delay = tree.FindNodes("Delay")[0];
        tree.SetDelay(delay, 0.25f, 0.5f);
        var node = TreeBlobReader.Node(tree.Data, delay);
        Assert.Equal(0.25f, BitConverter.ToSingle(tree.Data, node.BlackboardStart + Layout.DelayMinTimeOffset));
        Assert.Equal(0.5f, BitConverter.ToSingle(tree.Data, node.BlackboardStart + Layout.DelayMaxTimeOffset));

        Assert.Throws<InvalidOperationException>(() => tree.SetDelay(0, 1f, 2f));
        Assert.Throws<InvalidOperationException>(() => tree.SetParameter(420, TreeParameter.HealthChangePercentageAmount, 0.1f));
        Assert.Throws<ArgumentException>(() => tree.SetDelay(delay, 2f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.SetDelay(9999, 1f, 2f));

        int intSet = tree.FindNodes("Int_Set")[0];
        tree.SetParameter(intSet, TreeParameter.IntSetValue, 3f);
        Assert.Equal(3, BitConverter.ToInt32(tree.Data, TreeBlobReader.Node(tree.Data, intSet).BlackboardStart + Layout.IntSetValueOffset));
        Assert.Throws<ArgumentException>(() => tree.SetParameter(intSet, TreeParameter.IntSetValue, 1.5f));
        Assert.Equal(2, tree.Changes.Count);
    }

    [Fact]
    public void A_frozen_variant_refuses_every_edit()
    {
        var tree = Tree();
        tree.Freeze();
        Assert.True(tree.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => tree.DisableCombat());
        Assert.Throws<InvalidOperationException>(() => tree.SetDelay(tree.FindNodes("Delay")[0], 1f, 2f));
    }

    [Fact]
    public void A_blob_that_does_not_parse_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TreeVariant("b", 1, "t", 2, new byte[16]));
        var truncated = Wendigo.Value.Tree.Take(64).ToArray();
        Assert.Throws<ArgumentException>(() => new TreeVariant("b", 1, "t", 2, truncated));
    }

    // ---- phase script, history ------------------------------------------------------------------------

    [Fact]
    public void The_phase_script_decodes_and_rearms_only_above_the_new_threshold()
    {
        var phase = PhaseScriptCodec.Decode(Wendigo.Value.Phase);
        Assert.Equal(0f, phase.HealthFactor);
        Assert.Equal(-1720429443, phase.NewBuffGuid);
        Assert.False(phase.ThresholdMet);

        byte[] data = (byte[])Wendigo.Value.Phase.Clone();
        data[Layout.PhaseScriptThresholdMetOffset] = 1;
        var patches = PhaseScriptCodec.Write(data, 0.9f, 123, healthFactorNow: 1f);
        var moved = PhaseScriptCodec.Decode(data);
        Assert.Equal(0.9f, moved.HealthFactor);
        Assert.Equal(123, moved.NewBuffGuid);
        Assert.False(moved.ThresholdMet);
        Assert.Equal(3, patches.Count);

        byte[] low = (byte[])Wendigo.Value.Phase.Clone();
        low[Layout.PhaseScriptThresholdMetOffset] = 1;
        Assert.Single(PhaseScriptCodec.Write(low, 0.9f, null, healthFactorNow: 0.5f));
        Assert.True(PhaseScriptCodec.Decode(low).ThresholdMet);
    }

    [Fact]
    public void A_cast_history_row_decodes()
    {
        var row = new byte[Layout.CastHistoryStride];
        BitConverter.GetBytes(1238313774).CopyTo(row, Layout.CastHistoryGuidOffset);
        BitConverter.GetBytes(812.5).CopyTo(row, Layout.CastHistoryTimeOffset);
        BitConverter.GetBytes(1).CopyTo(row, Layout.CastHistoryResultOffset);
        var entry = CastHistoryCodec.Decode(row, g => g == 1238313774 ? "AB_Wendigo_FrostNova_AbilityGroup" : "");
        Assert.Equal(812.5, entry.CastTime);
        Assert.True(entry.Interrupted);
        Assert.Equal("FrostNova", AiNames.Short(entry.Name));
    }

    // ---- factions ---------------------------------------------------------------------------------------

    private static (int, int)? Masks(int index) => index switch
    {
        31 => (131072, 0),        // Faction_Wendigo: Ignored, friendly 0 (live read)
        0 => (64, 16448),         // Faction_Bandits: Bandits, friendly Bandits|VampireHunters
        _ => null,
    };

    [Fact]
    public void Two_Frostmaws_on_Faction_Wendigo_are_not_allies()
    {
        Assert.False(FactionMath.IsAllies(2, 31, 2, 31, Masks));
        Assert.False(new AiFactionInfo("Faction_Wendigo", 1, 31, 131072, 0, 0, 0).SelfFriendly);
    }

    [Fact]
    public void Two_units_on_a_self_friendly_faction_are_allies()
    {
        Assert.True(FactionMath.IsAllies(2, 0, 2, 0, Masks));
        Assert.True(new AiFactionInfo("Faction_Bandits", 1, 0, 64, 16448, 0, 0).SelfFriendly);
        Assert.False(FactionMath.IsAllies(2, 0, 2, 31, Masks));
    }

    [Fact]
    public void Neutral_and_unset_teams_are_never_allies_and_non_unit_teams_compare_values()
    {
        Assert.False(FactionMath.IsAllies(1, 0, 1, 0, Masks));
        Assert.False(FactionMath.IsAllies(0, 0, 2, 0, Masks));
        Assert.True(FactionMath.IsAllies(7, -1, 7, -1, Masks));
        Assert.False(FactionMath.IsAllies(7, -1, 8, -1, Masks));
        Assert.Null(FactionMath.IsAllies(2, 5, 2, 0, Masks));
    }

    // ---- variants ---------------------------------------------------------------------------------------

    [Fact]
    public void A_marker_id_is_the_plugin_guid_plus_the_variant_name_within_63_ascii_bytes()
    {
        Assert.Equal("progenitor.bossvariants.ai.Frenzied", VariantBook.MarkerId("progenitor.bossvariants", "Frenzied"));
        Assert.Throws<ArgumentException>(() => VariantBook.MarkerId("p", "bad name"));
        Assert.Throws<ArgumentException>(() => VariantBook.MarkerId("p", "a.b"));
        Assert.Throws<ArgumentException>(() => VariantBook.MarkerId(new string('x', 60), "long"));
    }

    [Fact]
    public void Reapply_plans_only_marked_units_of_the_right_prefab_not_yet_done_this_epoch()
    {
        var book = new VariantBook();
        book.Define("Frenzied", 24378719, 1001, "p.ai.Frenzied");
        book.Define("Sentinel", 24378719, 1002, "p.ai.Sentinel");
        Assert.Throws<InvalidOperationException>(() => book.Define("Frenzied", 1, 1003, "x"));
        Assert.Throws<InvalidOperationException>(() => book.Define("Other", 1, 1001, "x"));

        var scan = new[]
        {
            new VariantBook.ScannedUnit(10, 1, 24378719, new[] { 5, 1001 }),
            new VariantBook.ScannedUnit(11, 1, 24378719, new[] { 1002 }),
            new VariantBook.ScannedUnit(12, 1, 24378719, new[] { 5 }),
            new VariantBook.ScannedUnit(13, 1, 999, new[] { 1001 }),
            new VariantBook.ScannedUnit(14, 1, 24378719, new[] { 1001 }),
        };
        book.Record(VariantBook.Key(7, 14, 1), "Frenzied");
        var mismatched = new List<string>();
        var plan = book.PlanReapply(scan, 7, mismatched);
        Assert.Equal(new[] { ("Frenzied", 10), ("Sentinel", 11) }, plan.Select(p => (p.Variant.Name, p.Unit.Index)).ToArray());
        Assert.Single(mismatched);
        Assert.Contains("13:1", mismatched[0]);

        Assert.Equal("Frenzied", book.VariantOf(VariantBook.Key(7, 14, 1)));
        Assert.Equal(1, book.DropStale(8));
        Assert.Null(book.VariantOf(VariantBook.Key(7, 14, 1)));
        Assert.Equal(3, book.PlanReapply(scan, 8, new List<string>()).Count);
    }

    [Fact]
    public void A_variant_spec_validates_its_edits_when_they_are_added()
    {
        var spec = new VariantSpec("CHAR_Wendigo_VBlood");
        Assert.False(spec.UsesCastOptions);
        spec.HealthGate("SnowStorm", 1f).EditCastOption("FrostNova", new CastOptionEdit { CooldownSeconds = 8f });
        Assert.True(spec.UsesCastOptions);
        Assert.Throws<ArgumentOutOfRangeException>(() => spec.HealthGate("SnowStorm", 2f));
        Assert.Throws<ArgumentException>(() => spec.EditCastOption("FrostNova", new CastOptionEdit()));
        Assert.Throws<ArgumentException>(() => new VariantSpec(" "));
    }

    [Fact]
    public void State_names_read_the_flags()
    {
        Assert.Equal("Combat", AiNames.State(Layout.GenericEnemyStateCombat));
        Assert.Equal("Combat|Relocate_Unstuck", AiNames.State(262152));
        Assert.Equal("Initialize", AiNames.State(0));
        Assert.Equal("AttackEntityCastOption", AiNames.NodeType(Layout.BehaviourTreeNodeTypeAttackEntityCastOption));
        Assert.Equal("HealthFactor", AiNames.ConditionSource(Layout.ConditionSourceHealthFactor));
    }
}
