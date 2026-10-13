using System;
using Progenitor.Effects;
using Progenitor.Networking;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Units look, game-free half: <see cref="UnitLook"/> range rules, color composition
/// (<see cref="UnitLookMath"/>), the transition rule (<see cref="UnitLookTransition"/>), the wire
/// message (<see cref="UnitLookWire"/>) and the server/client bookkeeping
/// (<see cref="UnitLookServerTable"/>, <see cref="UnitLookClientTable"/>, <see cref="NaturalScaleBook"/>,
/// <see cref="UnitLookModelStep"/>). The bounds are the ones measured live on 2026-09-25
/// (internal research notes, "Size bounds", "Tint and glow bounds").
/// </summary>
public class UnitLookTests
{
    private static UnitLook Look(float scale = 1f, EffectColor? tint = null, float glow = 1f, EffectColor? overlay = null, float strength = 0f) => new()
    {
        VisualScale = scale,
        Tint = tint ?? new EffectColor(1f, 1f, 1f),
        GlowIntensity = glow,
        Overlay = overlay ?? new EffectColor(1f, 1f, 1f),
        OverlayStrength = strength,
    };

    // ---- ranges ---------------------------------------------------------------------------------

    [Fact]
    public void Default_look_is_vanilla_and_valid()
    {
        var look = new UnitLook();
        Assert.True(look.IsVanilla);
        look.Validate();
        Assert.Equal(1f, look.VisualScale);
        Assert.Equal(1f, look.GlowIntensity);
        Assert.Equal(0f, look.OverlayStrength);
        Assert.True(UnitLook.Vanilla.IsVanilla);
    }

    [Fact]
    public void Bounds_are_the_measured_ones()
    {
        Assert.Equal(0.1f, UnitLook.MinVisualScale);
        Assert.Equal(5f, UnitLook.MaxVisualScale);
        Assert.Equal(0.25f, UnitLook.RecommendedMinVisualScale);
        Assert.Equal(3f, UnitLook.RecommendedMaxVisualScale);
        Assert.Equal(1f, UnitLook.MaxColorChannel);
        Assert.Equal(0.3f, UnitLook.NearBlackTintChannel);
        Assert.Equal(1f, UnitLook.MinGlowIntensity);
        Assert.Equal(8f, UnitLook.MaxGlowIntensity);
        Assert.Equal(4f, UnitLook.RecommendedMinGlowIntensity);
        Assert.Equal(6f, UnitLook.RecommendedMaxGlowIntensity);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(3f)]
    [InlineData(5f)]
    public void Visual_scale_inside_the_hard_range_is_accepted(float scale) => Look(scale: scale).Validate();

    [Theory]
    [InlineData(0.09f)]
    [InlineData(5.01f)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Visual_scale_outside_the_hard_range_throws(float scale)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Look(scale: scale).Validate());
        Assert.Contains("VisualScale", e.Message);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    [InlineData(6f)]
    [InlineData(8f)]
    public void Glow_inside_the_hard_range_is_accepted(float glow) => Look(glow: glow).Validate();

    [Theory]
    [InlineData(0.99f)]
    [InlineData(8.01f)]
    [InlineData(10f)]
    [InlineData(float.NaN)]
    public void Glow_outside_the_hard_range_throws(float glow)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Look(glow: glow).Validate());
        Assert.Contains("GlowIntensity", e.Message);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 0.1f, 0.1f)]
    [InlineData(0.3f, 0.3f, 0.3f)]
    [InlineData(1f, 1f, 1f)]
    public void Tint_channels_zero_to_one_are_accepted(float r, float g, float b) =>
        Look(tint: new EffectColor(r, g, b)).Validate();

    [Theory]
    [InlineData(1.01f, 1f, 1f)]
    [InlineData(1f, -0.01f, 1f)]
    [InlineData(1f, 1f, float.NaN)]
    [InlineData(2f, 2f, 2f)]
    public void Tint_channel_outside_zero_to_one_throws(float r, float g, float b)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Look(tint: new EffectColor(r, g, b)).Validate());
        Assert.Contains("Tint", e.Message);
        Assert.Contains("GlowIntensity", e.Message); // points at the right knob for brighter
    }

    [Fact]
    public void Tint_alpha_is_ignored()
    {
        var look = Look(tint: new EffectColor(1f, 1f, 1f, 0f));
        look.Validate();
        Assert.True(look.IsVanilla);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Overlay_strength_zero_to_one_is_accepted(float strength) => Look(strength: strength).Validate();

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void Overlay_strength_outside_zero_to_one_throws(float strength)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Look(strength: strength).Validate());
        Assert.Contains("OverlayStrength", e.Message);
    }

    [Fact]
    public void Overlay_channel_above_one_throws()
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Look(overlay: new EffectColor(1.5f, 0f, 0f), strength: 1f).Validate());
        Assert.Contains("Overlay", e.Message);
    }

    [Fact]
    public void Refusal_of_null_is_a_message_not_a_throw() => Assert.NotNull(UnitLookMath.Refusal(null));

    // ---- composition ----------------------------------------------------------------------------

    [Fact]
    public void Base_colour_is_tint_times_glow_on_a_white_material()
    {
        var look = Look(tint: new EffectColor(1f, 0.25f, 0.25f), glow: 4f);
        var value = UnitLookMath.ComposeBase(LookRgba.White, look);
        Assert.Equal(new LookRgba(4f, 1f, 1f, 1f), value);
    }

    [Fact]
    public void Base_colour_keeps_the_material_alpha_and_multiplies_its_rgb()
    {
        var look = Look(tint: new EffectColor(0.5f, 1f, 1f, 0f), glow: 2f);
        var value = UnitLookMath.ComposeBase(new LookRgba(0.5f, 0.5f, 1f, 0.75f), look);
        Assert.Equal(new LookRgba(0.5f, 1f, 2f, 0.75f), value);
    }

    [Fact]
    public void Glow_alone_on_white_gives_a_white_hdr_colour()
    {
        var value = UnitLookMath.ComposeBase(LookRgba.White, Look(glow: 5f));
        Assert.Equal(new LookRgba(5f, 5f, 5f, 1f), value);
    }

    [Fact]
    public void Overlay_is_its_rgb_with_strength_as_alpha()
    {
        var value = UnitLookMath.ComposeOverlay(Look(overlay: new EffectColor(0.2f, 0.4f, 1f, 1f), strength: 0.5f));
        Assert.Equal(new LookRgba(0.2f, 0.4f, 1f, 0.5f), value);
    }

    [Fact]
    public void Which_properties_a_look_touches()
    {
        Assert.False(UnitLookMath.TouchesBaseColor(Look(scale: 2f)));
        Assert.False(UnitLookMath.TouchesOverlay(Look(scale: 2f)));
        Assert.True(UnitLookMath.TouchesBaseColor(Look(tint: new EffectColor(1f, 0.9f, 1f))));
        Assert.True(UnitLookMath.TouchesBaseColor(Look(glow: 1.5f)));
        Assert.False(UnitLookMath.TouchesOverlay(Look(overlay: new EffectColor(1f, 0f, 0f))));
        Assert.True(UnitLookMath.TouchesOverlay(Look(strength: 0.1f)));
        Assert.False(Look(scale: 2f).IsVanilla);
        Assert.False(Look(strength: 0.1f).IsVanilla);
    }

    // ---- transitions (diff and reset) ------------------------------------------------------------

    [Fact]
    public void A_scale_only_look_never_touches_colours()
    {
        var t = UnitLookTransition.Plan(null, Look(scale: 2f));
        Assert.True(t.WriteScale);
        Assert.False(t.WriteBase || t.RestoreBase || t.WriteOverlay || t.RestoreOverlay);
    }

    [Fact]
    public void Removing_a_tint_restores_base_colour_once_and_leaves_the_overlay_alone()
    {
        var tint = Look(tint: new EffectColor(1f, 0.1f, 0.1f));
        var t = UnitLookTransition.Plan(tint, null);
        Assert.True(t.RestoreBase);
        Assert.False(t.WriteBase);
        Assert.False(t.WriteOverlay || t.RestoreOverlay);
        Assert.False(t.WriteScale);

        // After the restore is committed nothing is left to do.
        Assert.True(UnitLookTransition.Plan(null, null).IsEmpty);
    }

    [Fact]
    public void Switching_from_overlay_to_glow_writes_base_and_restores_the_overlay()
    {
        var from = Look(overlay: new EffectColor(1f, 0f, 0f), strength: 1f);
        var to = Look(glow: 5f);
        var t = UnitLookTransition.Plan(from, to);
        Assert.True(t.WriteBase);
        Assert.False(t.RestoreBase);
        Assert.False(t.WriteOverlay);
        Assert.True(t.RestoreOverlay);
    }

    [Fact]
    public void A_steady_look_keeps_writing_what_it_uses_so_drift_is_repaired()
    {
        var look = Look(tint: new EffectColor(0.3f, 0.3f, 1f), strength: 0.5f);
        var t = UnitLookTransition.Plan(look, look);
        Assert.True(t.WriteBase);
        Assert.True(t.WriteOverlay);
        Assert.False(t.RestoreBase || t.RestoreOverlay);
    }

    [Fact]
    public void Scale_back_to_one_is_still_written()
    {
        var t = UnitLookTransition.Plan(Look(scale: 3f), Look(glow: 2f));
        Assert.True(t.WriteScale);
    }

    [Fact]
    public void Same_compares_fields_not_references()
    {
        Assert.True(UnitLookMath.Same(Look(scale: 2f), Look(scale: 2f)));
        Assert.False(UnitLookMath.Same(Look(scale: 2f), Look(scale: 2.5f)));
        Assert.False(UnitLookMath.Same(Look(), null));
        // Alpha is not part of the look.
        Assert.True(UnitLookMath.Same(Look(tint: new EffectColor(1f, 0f, 0f, 0f)), Look(tint: new EffectColor(1f, 0f, 0f, 1f))));
    }

    // ---- wire -----------------------------------------------------------------------------------

    [Fact]
    public void Event_id_is_PruL_in_the_reserved_range()
    {
        Assert.Equal(0x5072754C, UnitLookWire.EventId);
        Assert.Equal("PruL", new string(new[]
        {
            (char)((UnitLookWire.EventId >> 24) & 0xFF), (char)((UnitLookWire.EventId >> 16) & 0xFF),
            (char)((UnitLookWire.EventId >> 8) & 0xFF), (char)(UnitLookWire.EventId & 0xFF),
        }));
        Assert.True(CustomEventPolicy.IsReserved(UnitLookWire.EventId));
        Assert.NotEqual(CustomEventPolicy.StopServerEventId, UnitLookWire.EventId);
    }

    [Fact]
    public void Message_is_fixed_size() => Assert.Equal(48, UnitLookWire.Size);

    [Fact]
    public void Set_round_trips_through_bytes()
    {
        var look = Look(scale: 2.5f, tint: new EffectColor(1f, 0.25f, 0.25f), glow: 4.5f,
            overlay: new EffectColor(0f, 0.5f, 1f), strength: 0.5f);
        var key = new UnitNetKey(12345, 7);
        byte[] bytes = UnitLookWire.ToBytes(UnitLookWire.Set(key, look));
        Assert.Equal(UnitLookWire.Size, bytes.Length);
        Assert.True(UnitLookWire.TryFromBytes(bytes, out var message));

        Assert.True(UnitLookWire.TryDecode(message, out var op, out var decodedKey, out var decoded, out string? why), why);
        Assert.Equal(UnitLookOp.Set, op);
        Assert.Equal(key, decodedKey);
        Assert.NotNull(decoded);
        Assert.True(UnitLookMath.Same(look, decoded));
    }

    [Fact]
    public void Clear_and_clear_all_decode_without_a_look()
    {
        Assert.True(UnitLookWire.TryDecode(UnitLookWire.Clear(new UnitNetKey(9, 1)), out var op, out var key, out var look, out _));
        Assert.Equal(UnitLookOp.Clear, op);
        Assert.Equal(new UnitNetKey(9, 1), key);
        Assert.Null(look);

        Assert.True(UnitLookWire.TryDecode(UnitLookWire.ClearAll(), out op, out _, out look, out _));
        Assert.Equal(UnitLookOp.ClearAll, op);
        Assert.Null(look);
    }

    [Fact]
    public void Decode_refuses_another_schema_an_unknown_op_and_an_invalid_id()
    {
        var wrongSchema = UnitLookWire.Set(new UnitNetKey(1, 0), Look(scale: 2f));
        wrongSchema.Schema = 2;
        Assert.False(UnitLookWire.TryDecode(wrongSchema, out _, out _, out _, out string? why));
        Assert.Contains("schema", why);

        var unknownOp = UnitLookWire.Set(new UnitNetKey(1, 0), Look(scale: 2f));
        unknownOp.Op = 9;
        Assert.False(UnitLookWire.TryDecode(unknownOp, out _, out _, out _, out why));
        Assert.Contains("op", why);

        Assert.False(UnitLookWire.TryDecode(UnitLookWire.Set(new UnitNetKey(0, 0), Look(scale: 2f)), out _, out _, out _, out why));
        Assert.Contains("network id", why);
        Assert.False(UnitLookWire.TryDecode(UnitLookWire.Clear(new UnitNetKey(-4, 0)), out _, out _, out _, out _));
    }

    [Fact]
    public void Decode_refuses_a_look_the_api_would_have_refused()
    {
        var message = UnitLookWire.Set(new UnitNetKey(3, 0), Look(glow: 4f));
        message.GlowIntensity = 50f;
        Assert.False(UnitLookWire.TryDecode(message, out var op, out _, out var look, out string? why));
        Assert.Equal(UnitLookOp.None, op);
        Assert.Null(look);
        Assert.Contains("GlowIntensity", why);

        message = UnitLookWire.Set(new UnitNetKey(3, 0), Look(scale: 2f));
        message.VisualScale = float.NaN;
        Assert.False(UnitLookWire.TryDecode(message, out _, out _, out _, out _));
    }

    [Fact]
    public void Bytes_of_the_wrong_length_are_refused()
    {
        Assert.False(UnitLookWire.TryFromBytes(new byte[UnitLookWire.Size - 1], out _));
        Assert.False(UnitLookWire.TryFromBytes(new byte[UnitLookWire.Size + 4], out _));
    }

    // ---- server table ---------------------------------------------------------------------------

    [Fact]
    public void Server_table_marks_new_and_changed_looks_dirty()
    {
        var table = new UnitLookServerTable();
        var unit = new UnitId(100, 1);
        var entry = table.Set(unit, Look(scale: 2f), "mod.a", epoch: 1);
        Assert.True(entry.Dirty);

        // Sent once, the same look again is not re-sent.
        entry.Dirty = false;
        entry.Sent = true;
        entry.Key = new UnitNetKey(55, 0);
        Assert.False(table.Set(unit, Look(scale: 2f), "mod.a", epoch: 1).Dirty);

        // A different look is.
        var changed = table.Set(unit, Look(scale: 3f), "mod.a", epoch: 1);
        Assert.Same(entry, changed);
        Assert.True(changed.Dirty);
        Assert.Equal(new UnitNetKey(55, 0), changed.Key);
    }

    [Fact]
    public void Server_table_starts_over_on_a_new_world_epoch()
    {
        var table = new UnitLookServerTable();
        var unit = new UnitId(100, 1);
        var first = table.Set(unit, Look(scale: 2f), "mod.a", epoch: 1);
        first.Key = new UnitNetKey(55, 0);
        first.Sent = true;
        var second = table.Set(unit, Look(scale: 2f), "mod.a", epoch: 2);
        Assert.NotSame(first, second);
        Assert.False(second.Key.IsValid);
        Assert.True(second.Dirty);
    }

    [Fact]
    public void Server_table_get_returns_a_copy()
    {
        var table = new UnitLookServerTable();
        var unit = new UnitId(7, 1);
        var look = Look(scale: 2f);
        table.Set(unit, look, "mod.a", 1);
        look.VisualScale = 4f; // the caller's object is not the stored one
        var got = table.Get(unit)!;
        Assert.Equal(2f, got.VisualScale);
        got.VisualScale = 0.5f;
        Assert.Equal(2f, table.Get(unit)!.VisualScale);
        Assert.Null(table.Get(new UnitId(8, 1)));
    }

    [Fact]
    public void Server_table_removes_by_unit_and_by_plugin()
    {
        var table = new UnitLookServerTable();
        table.Set(new UnitId(1, 1), Look(scale: 2f), "mod.a", 1);
        table.Set(new UnitId(2, 1), Look(scale: 2f), "mod.b", 1);
        table.Set(new UnitId(3, 1), Look(scale: 2f), "mod.a", 1);

        Assert.NotNull(table.Remove(new UnitId(2, 1)));
        Assert.Null(table.Remove(new UnitId(2, 1)));
        var owned = table.RemoveOwnedBy("mod.a");
        Assert.Equal(2, owned.Count);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Character_looks_are_removed_when_their_user_disconnects()
    {
        var table = new UnitLookServerTable();
        var alice = new UnitId(900, 1);
        var bob = new UnitId(901, 1);
        table.Set(new UnitId(10, 1), Look(scale: 1.5f), "mod.a", 1, alice);   // Alice's character
        table.Set(new UnitId(11, 1), Look(scale: 1.5f), "mod.a", 1, bob);     // Bob's character
        table.Set(new UnitId(12, 1), Look(scale: 2f), "mod.a", 1);            // a spawned unit

        Assert.Empty(table.RemoveByUser(UnitId.None));
        var gone = table.RemoveByUser(alice);
        Assert.Single(gone);
        Assert.Equal(new UnitId(10, 1), gone[0].Unit);
        Assert.Equal(2, table.Count);
        Assert.Empty(table.RemoveByUser(alice));
        Assert.Null(table.Get(new UnitId(10, 1)));
        Assert.NotNull(table.Get(new UnitId(11, 1)));
        Assert.NotNull(table.Get(new UnitId(12, 1)));
    }

    [Fact]
    public void A_unit_set_through_its_handle_is_not_tied_to_a_user()
    {
        var table = new UnitLookServerTable();
        var unit = new UnitId(10, 1);
        Assert.True(table.Set(unit, Look(scale: 2f), "mod.a", 1).CharacterUser.IsNone);
        // The same entity set again by entity for a player, then by handle: the last call wins.
        Assert.Equal(new UnitId(900, 1), table.Set(unit, Look(scale: 2f), "mod.a", 1, new UnitId(900, 1)).CharacterUser);
        Assert.True(table.Set(unit, Look(scale: 2f), "mod.b", 1).CharacterUser.IsNone);
        Assert.Empty(table.RemoveByUser(new UnitId(900, 1)));
    }

    [Fact]
    public void Character_looks_are_released_with_their_plugin()
    {
        var table = new UnitLookServerTable();
        table.Set(new UnitId(10, 1), Look(scale: 1.5f), "mod.a", 1, new UnitId(900, 1));
        table.Set(new UnitId(11, 1), Look(scale: 1.5f), "mod.b", 1, new UnitId(901, 1));
        Assert.Single(table.RemoveOwnedBy("mod.a"));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void A_player_model_scales_from_its_natural_one()
    {
        // Game default (live 2026-09-25): CHAR_VampireMale's hybrid model transform is at localScale 1.
        var book = new NaturalScaleBook();
        var entry = new UnitLookClientTable().Set(new UnitNetKey(3, 0), Look(scale: 1.5f));
        var step = UnitLookModelStep.Plan(entry, 42, new UnitVec3(1f, 1f, 1f), book);
        Assert.Equal(new UnitVec3(1.5f, 1.5f, 1.5f), step.TargetScale);
        Assert.True(step.WriteScale);
        UnitLookModelStep.Commit(entry, 42);

        // A reconnecting viewer or a shapeshift gives a new model instance: it is scaled from its own natural size.
        var form = UnitLookModelStep.Plan(entry, 43, new UnitVec3(0.8f, 0.8f, 0.8f), book);
        Assert.True(form.ModelChanged);
        Assert.Equal(42, form.PreviousModelId);
        Assert.Equal(1.2f, form.TargetScale.X, 5);
    }

    // ---- client bookkeeping -----------------------------------------------------------------------

    [Fact]
    public void Natural_scale_is_recorded_on_first_sight_only()
    {
        var book = new NaturalScaleBook();
        Assert.Equal(new UnitVec3(2f, 2f, 2f), book.Observe(41, new UnitVec3(2f, 2f, 2f)));
        // Later observations see our own writes; the natural value does not follow them.
        Assert.Equal(new UnitVec3(2f, 2f, 2f), book.Observe(41, new UnitVec3(6f, 6f, 6f)));
        Assert.True(book.TryGet(41, out var natural));
        Assert.Equal(new UnitVec3(2f, 2f, 2f), natural);

        Assert.True(book.Forget(41));
        Assert.False(book.TryGet(41, out _));
        Assert.Equal(new UnitVec3(1.2f, 1.2f, 1.2f), book.Observe(41, new UnitVec3(1.2f, 1.2f, 1.2f)));
    }

    [Fact]
    public void First_apply_scales_relative_to_the_models_natural_size()
    {
        var book = new NaturalScaleBook();
        var table = new UnitLookClientTable();
        var entry = table.Set(new UnitNetKey(10, 0), Look(scale: 1.5f, tint: new EffectColor(1f, 0.2f, 0.2f)));

        // Frostmaw's model is naturally 2.
        var step = UnitLookModelStep.Plan(entry, modelId: 500, currentScale: new UnitVec3(2f, 2f, 2f), book);
        Assert.True(step.ModelChanged);
        Assert.Equal(0, step.PreviousModelId);
        Assert.Equal(new UnitVec3(2f, 2f, 2f), step.Natural);
        Assert.Equal(new UnitVec3(3f, 3f, 3f), step.TargetScale);
        Assert.True(step.WriteScale);
        Assert.True(step.Colors.WriteBase);
        UnitLookModelStep.Commit(entry, 500);

        // Next pass: the model is at the target; nothing to write for scale.
        var steady = UnitLookModelStep.Plan(entry, 500, new UnitVec3(3f, 3f, 3f), book);
        Assert.False(steady.ModelChanged);
        Assert.False(steady.WriteScale);
        Assert.True(steady.Colors.WriteBase);
    }

    [Fact]
    public void A_reset_scale_is_written_again()
    {
        var book = new NaturalScaleBook();
        var entry = new UnitLookClientTable().Set(new UnitNetKey(10, 0), Look(scale: 0.5f));
        UnitLookModelStep.Plan(entry, 500, new UnitVec3(1.2f, 1.2f, 1.2f), book);
        UnitLookModelStep.Commit(entry, 500);

        var drift = UnitLookModelStep.Plan(entry, 500, new UnitVec3(1.2f, 1.2f, 1.2f), book);
        Assert.True(drift.WriteScale);
        Assert.Equal(0.6f, drift.TargetScale.X, 5);
    }

    [Fact]
    public void A_new_model_instance_starts_from_vanilla_and_names_the_old_one()
    {
        var book = new NaturalScaleBook();
        var entry = new UnitLookClientTable().Set(new UnitNetKey(10, 0), Look(strength: 1f, overlay: new EffectColor(1f, 0f, 0f)));
        UnitLookModelStep.Plan(entry, 500, new UnitVec3(1f, 1f, 1f), book);
        UnitLookModelStep.Commit(entry, 500);

        var rebuilt = UnitLookModelStep.Plan(entry, 777, new UnitVec3(1f, 1f, 1f), book);
        Assert.True(rebuilt.ModelChanged);
        Assert.Equal(500, rebuilt.PreviousModelId);
        Assert.True(rebuilt.Colors.WriteOverlay);
        Assert.False(rebuilt.Colors.RestoreOverlay);
        Assert.True(book.TryGet(777, out _));
    }

    [Fact]
    public void Restore_returns_the_natural_scale_and_the_colours_to_undo()
    {
        var book = new NaturalScaleBook();
        var applied = Look(scale: 3f, glow: 5f);
        book.Observe(500, new UnitVec3(2f, 2f, 2f));
        var (hasNatural, natural, colors) = UnitLookModelStep.Restore(500, applied, book);
        Assert.True(hasNatural);
        Assert.Equal(new UnitVec3(2f, 2f, 2f), natural);
        Assert.True(colors.RestoreBase);
        Assert.False(colors.RestoreOverlay);
        Assert.False(colors.WriteBase);

        var (unknown, _, _) = UnitLookModelStep.Restore(999, applied, book);
        Assert.False(unknown);
    }

    [Fact]
    public void Client_table_keeps_what_was_applied_when_the_wanted_look_changes()
    {
        var table = new UnitLookClientTable();
        var key = new UnitNetKey(3, 2);
        var entry = table.Set(key, Look(glow: 4f));
        UnitLookModelStep.Commit(entry, 11);
        var again = table.Set(key, Look(scale: 2f));
        Assert.Same(entry, again);
        Assert.Equal(11, again.ModelId);
        Assert.Equal(4f, again.Applied!.GlowIntensity);

        // Glow removed: the next plan restores base color on the same model.
        var step = UnitLookModelStep.Plan(again, 11, new UnitVec3(1f, 1f, 1f), new NaturalScaleBook());
        Assert.True(step.Colors.RestoreBase);
        Assert.True(step.WriteScale);

        Assert.Same(entry, table.Remove(key));
        Assert.Null(table.Get(key));
        table.Set(new UnitNetKey(4, 0), Look(scale: 2f));
        table.Set(new UnitNetKey(5, 0), Look(scale: 2f));
        Assert.Equal(2, table.RemoveAll().Count);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Spawn_options_have_no_look_by_default() => Assert.Null(new UnitSpawnOptions().Look);
}
