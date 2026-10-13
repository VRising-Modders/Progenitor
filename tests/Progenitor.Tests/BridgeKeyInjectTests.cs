using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>key</c> bridge op: arguments (<see cref="KeyInjectPlan"/>), the release guarantees
/// (<see cref="KeyHoldLedger"/>) and the bytes of the queued Input System events (<see cref="KeyEventBytes"/>).
/// </summary>
public class BridgeKeyInjectTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static UiKey[] K(params UiKey[] keys) => keys;

    // ---- arguments ----

    [Fact]
    public void Tap_of_one_key_uses_the_default_length()
    {
        var r = KeyInjectPlan.Parse(Args("{\"action\":\"tap\",\"keys\":\"M\"}"));
        Assert.Equal(KeyInjectPlan.Action.Tap, r.Action);
        Assert.Equal(K(UiKey.M), r.Keys);
        Assert.Equal(KeyInjectPlan.TapDefaultMs, r.Ms);
        Assert.False(r.Background);
    }

    [Fact]
    public void A_chord_presses_modifiers_first()
    {
        var r = KeyInjectPlan.Parse(Args("{\"action\":\"hold\",\"keys\":\"r + Ctrl\",\"ms\":750}"));
        Assert.Equal(K(UiKey.LeftCtrl, UiKey.R), r.Keys);
        Assert.Equal(750, r.Ms);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F12", new[] { UiKey.LeftCtrl, UiKey.LeftShift, UiKey.F12 })]
    [InlineData("RightCtrl+Enter", new[] { UiKey.RightCtrl, UiKey.Enter })]
    [InlineData("AltGr+7", new[] { UiKey.RightAlt, UiKey.Digit7 })]
    [InlineData("LeftShift", new[] { UiKey.LeftShift })]
    [InlineData("Esc", new[] { UiKey.Escape })]
    [InlineData("Ctrl+ctrl+C", new[] { UiKey.LeftCtrl, UiKey.C })]
    public void Key_names_follow_the_cfg_names(string keys, UiKey[] expected)
    {
        Assert.True(KeyInjectPlan.TryParseKeys(keys, out var parsed, out string why), why);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("A+B")]
    [InlineData("NotAKey")]
    [InlineData("MouseBack")]
    [InlineData("None")]
    public void Bad_key_names_are_refused(string keys) =>
        Assert.False(KeyInjectPlan.TryParseKeys(keys, out _, out _));

    [Theory]
    [InlineData("{\"action\":\"down\",\"keys\":\"W\"}", KeyInjectPlan.DownDefaultMs)]
    [InlineData("{\"action\":\"down\",\"keys\":\"W\",\"ms\":60000}", 60000)]
    [InlineData("{\"action\":\"hold\",\"keys\":\"W\"}", KeyInjectPlan.HoldDefaultMs)]
    public void Down_and_hold_carry_a_release_time(string json, int ms) =>
        Assert.Equal(ms, KeyInjectPlan.Parse(Args(json)).Ms);

    [Fact]
    public void Type_carries_text_and_background_opt_in()
    {
        var r = KeyInjectPlan.Parse(Args("{\"action\":\"type\",\"text\":\"hello world\",\"background\":true}"));
        Assert.Equal(KeyInjectPlan.Action.Type, r.Action);
        Assert.Equal("hello world", r.Text);
        Assert.True(r.Background);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"action\":\"press\",\"keys\":\"M\"}")]
    [InlineData("{\"action\":\"tap\"}")]
    [InlineData("{\"action\":\"tap\",\"keys\":\"M\",\"ms\":0}")]
    [InlineData("{\"action\":\"tap\",\"keys\":\"M\",\"ms\":10001}")]
    [InlineData("{\"action\":\"hold\",\"keys\":\"M\",\"ms\":\"200\"}")]
    [InlineData("{\"action\":\"down\",\"keys\":\"M\",\"ms\":60001}")]
    [InlineData("{\"action\":\"up\",\"keys\":\"M\",\"ms\":5}")]
    [InlineData("{\"action\":\"tap\",\"keys\":\"M\",\"text\":\"x\"}")]
    [InlineData("{\"action\":\"type\"}")]
    [InlineData("{\"action\":\"type\",\"text\":\"\"}")]
    [InlineData("{\"action\":\"type\",\"text\":\"x\",\"keys\":\"M\"}")]
    [InlineData("{\"action\":\"release\",\"keys\":\"M\"}")]
    [InlineData("{\"action\":\"status\",\"ms\":1}")]
    [InlineData("{\"action\":\"tap\",\"keys\":\"M\",\"background\":\"yes\"}")]
    public void Bad_requests_are_refused_with_the_usage(string json)
    {
        var e = Assert.Throws<ArgumentException>(() => KeyInjectPlan.Parse(Args(json)));
        Assert.Contains("key", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_longer_than_the_limit_is_refused()
    {
        string json = "{\"action\":\"type\",\"text\":\"" + new string('a', KeyInjectPlan.MaxTextLength + 1) + "\"}";
        Assert.Throws<ArgumentException>(() => KeyInjectPlan.Parse(Args(json)));
    }

    [Fact]
    public void The_op_refuses_outside_a_client() =>
        Assert.Equal(KeyInjectOps.ClientOnlyMessage,
            Assert.Throws<InvalidOperationException>(() => KeyInjectOps.Run(Args("{\"action\":\"tap\",\"keys\":\"M\"}"))).Message);

    // ---- release guarantees ----

    [Fact]
    public void Every_press_is_released_by_its_deadline()
    {
        var ledger = new KeyHoldLedger();
        Assert.Equal(K(UiKey.LeftCtrl, UiKey.R), ledger.Press(K(UiKey.LeftCtrl, UiKey.R), releaseAtMs: 200));
        Assert.Empty(ledger.Expired(199));
        Assert.Equal(K(UiKey.R, UiKey.LeftCtrl), ledger.Expired(200)); // main key first, as a player lets go
        Assert.Equal(0, ledger.Count);
        Assert.Null(ledger.NextDeadlineMs);
    }

    [Fact]
    public void Pressing_a_held_key_again_keeps_the_later_deadline_and_sends_no_second_down()
    {
        var ledger = new KeyHoldLedger();
        ledger.Press(K(UiKey.W), 100);
        Assert.Empty(ledger.Press(K(UiKey.W), 500));
        Assert.Empty(ledger.Press(K(UiKey.W), 300));
        Assert.Empty(ledger.Expired(499));
        Assert.Equal(K(UiKey.W), ledger.Expired(500));
    }

    [Fact]
    public void Up_releases_only_held_keys()
    {
        var ledger = new KeyHoldLedger();
        ledger.Press(K(UiKey.LeftShift, UiKey.W), 10_000);
        Assert.Equal(K(UiKey.W), ledger.Release(K(UiKey.W, UiKey.Q)));
        Assert.True(ledger.IsHeld(UiKey.LeftShift));
        Assert.False(ledger.IsHeld(UiKey.W));
    }

    [Fact]
    public void Release_all_lets_go_of_everything_main_keys_first()
    {
        var ledger = new KeyHoldLedger();
        ledger.Press(K(UiKey.LeftCtrl, UiKey.R), 10_000);
        ledger.Press(K(UiKey.W), 60_000);
        var up = ledger.ReleaseAll();
        Assert.Equal(3, up.Count);
        Assert.Equal(UiKey.LeftCtrl, up.Last());
        Assert.Equal(0, ledger.Count);
        Assert.Empty(ledger.ReleaseAll());
    }

    [Fact]
    public void A_down_has_a_deadline_too()
    {
        var ledger = new KeyHoldLedger();
        ledger.Press(K(UiKey.W), KeyInjectPlan.DownDefaultMs);
        Assert.Equal(KeyInjectPlan.DownDefaultMs, ledger.NextDeadlineMs);
        Assert.Equal(K(UiKey.W), ledger.Expired(KeyInjectPlan.DownDefaultMs));
    }

    // ---- event bytes ----

    [Fact]
    public void Keyboard_state_sets_one_bit_per_held_key()
    {
        // M is Key 27 (client metadata: KeyboardState "m" bit 27); LeftCtrl is 55.
        var state = KeyEventBytes.KeyboardState(new[] { 27, 55 });
        Assert.Equal(KeyEventBytes.KeyboardStateBytes, state.Length);
        Assert.Equal(1 << 3, state[3]);
        Assert.Equal(1 << 7, state[6]);
        Assert.Equal(2, state.Sum(b => System.Numerics.BitOperations.PopCount(b)));
        Assert.All(KeyEventBytes.KeyboardState(Array.Empty<int>()), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(110)]
    [InlineData(-1)]
    public void Bits_outside_the_keyboard_are_refused(int bit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyEventBytes.KeyboardState(new[] { bit }));

    [Fact]
    public void State_event_matches_the_dump_layout()
    {
        var state = KeyEventBytes.KeyboardState(new[] { 27 });
        var e = KeyEventBytes.KeyboardStateEvent(deviceId: 3, time: 12.5, state);
        Assert.Equal(40, e.Length);
        Assert.Equal(1398030676, BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x0)));       // StateEvent.Type 'STAT'
        Assert.Equal(38, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x4)));               // 0x18 + 14
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x6)));
        Assert.Equal(12.5, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(0x8))));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x10)));                // eventId: assigned on queue
        Assert.Equal(KeyEventBytes.KeyboardFormat, BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x14)));
        Assert.Equal(state, e.Skip(0x18).Take(14).ToArray());
        Assert.Equal(new byte[] { (byte)'S', (byte)'Y', (byte)'E', (byte)'K' }, e.Skip(0x14).Take(4).ToArray());
    }

    [Fact]
    public void Text_event_matches_the_dump_layout()
    {
        var e = KeyEventBytes.TextEvent(deviceId: 7, time: 1, character: 'h');
        Assert.Equal(24, e.Length);
        Assert.Equal(1413830740, BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x0)));       // TextEvent.Type 'TEXT'
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x4)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x6)));
        Assert.Equal('h', BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x14)));
    }

    [Fact]
    public void Device_ids_outside_ushort_are_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyEventBytes.TextEvent(70_000, 0, 'x'));
}
