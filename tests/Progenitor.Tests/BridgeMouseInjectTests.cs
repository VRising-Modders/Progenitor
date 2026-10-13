using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The <c>mouse</c> bridge op's game-free half: argument parsing and the mouse state event bytes.</summary>
public sealed class BridgeMouseInjectTests
{
    private static MouseInjectPlan.Request Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MouseInjectPlan.Parse(doc.RootElement.Clone());
    }

    [Fact]
    public void Click_defaults_to_the_left_button_and_80_ms()
    {
        var r = Parse("{\"action\":\"click\",\"x\":100,\"y\":200}");
        Assert.Equal(MouseInjectPlan.Action.Click, r.Action);
        Assert.Equal(MouseInjectPlan.Button.Left, r.Button);
        Assert.Equal(80, r.Ms);
        Assert.Equal(100f, r.X);
        Assert.Equal(200f, r.Y);
        Assert.Equal(MouseInjectPlan.SettleDefaultFrames, r.SettleFrames);
    }

    [Fact]
    public void Click_at_the_chat_target_takes_no_coordinates()
    {
        var r = Parse("{\"action\":\"click\",\"target\":\"chat\",\"button\":\"right\",\"ms\":300}");
        Assert.Equal(MouseInjectPlan.TargetChat, r.Target);
        Assert.Equal(MouseInjectPlan.Button.Right, r.Button);
        Assert.Equal(300, r.Ms);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"target\":\"chat\",\"x\":1,\"y\":1}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"target\":\"map\"}"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"press\"}")]
    [InlineData("{\"action\":\"click\"}")]
    [InlineData("{\"action\":\"click\",\"x\":10}")]
    [InlineData("{\"action\":\"click\",\"x\":-1,\"y\":5}")]
    [InlineData("{\"action\":\"click\",\"x\":1,\"y\":1,\"button\":\"side\"}")]
    [InlineData("{\"action\":\"click\",\"x\":1,\"y\":1,\"ms\":0}")]
    [InlineData("{\"action\":\"click\",\"x\":1,\"y\":1,\"ms\":20000}")]
    [InlineData("{\"action\":\"move\",\"x\":1,\"y\":1,\"button\":\"left\"}")]
    [InlineData("{\"action\":\"move\",\"x\":1,\"y\":1,\"ms\":10}")]
    [InlineData("{\"action\":\"status\",\"x\":1}")]
    [InlineData("{\"action\":\"release\",\"button\":\"left\"}")]
    [InlineData("{\"action\":\"click\",\"x\":1,\"y\":1,\"background\":\"yes\"}")]
    public void Bad_requests_are_refused(string json) =>
        Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void Drag_needs_an_end_point_and_takes_steps()
    {
        var r = Parse("{\"action\":\"drag\",\"x\":10,\"y\":20,\"toX\":110,\"toY\":220,\"steps\":30}");
        Assert.Equal(MouseInjectPlan.Action.Drag, r.Action);
        Assert.Equal(110f, r.ToX);
        Assert.Equal(220f, r.ToY);
        Assert.Equal(30, r.Steps);
        Assert.Equal(MouseInjectPlan.DragDefaultSteps, Parse("{\"action\":\"drag\",\"x\":1,\"y\":1,\"toX\":2,\"toY\":2}").Steps);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"drag\",\"x\":1,\"y\":1}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"x\":1,\"y\":1,\"toX\":2,\"toY\":2}"));
    }

    /// <summary>WHEEL1: a wheel takes a point and a non-zero notch count; other actions refuse notches.</summary>
    [Fact]
    public void Wheel_takes_a_point_and_notches()
    {
        var r = Parse("{\"action\":\"wheel\",\"x\":10,\"y\":20,\"notches\":-3}");
        Assert.Equal(MouseInjectPlan.Action.Wheel, r.Action);
        Assert.Equal(-3, r.Notches);
        Assert.Equal(10f, r.X);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"x\":1,\"y\":1}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"x\":1,\"y\":1,\"notches\":0}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"x\":1,\"y\":1,\"notches\":21}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"move\",\"x\":1,\"y\":1,\"notches\":1}"));
        var e = MouseEventBytes.MouseStateEvent(deviceId: 1, time: 0, x: 0, y: 0, buttons: 0, scrollY: -240f);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(0x18 + 0x10)));   // scroll.x
        Assert.Equal(-240f, BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(0x18 + 0x14))); // scroll.y
    }

    [Fact]
    public void Status_and_release_parse_bare()
    {
        Assert.Equal(MouseInjectPlan.Action.Status, Parse("{\"action\":\"status\"}").Action);
        Assert.Equal(MouseInjectPlan.Action.Release, Parse("{\"action\":\"release\"}").Action);
    }

    [Fact]
    public void Button_bits_follow_the_input_system_mouse_state()
    {
        Assert.Equal(1, MouseEventBytes.Bit(MouseInjectPlan.Button.Left));
        Assert.Equal(2, MouseEventBytes.Bit(MouseInjectPlan.Button.Right));
        Assert.Equal(4, MouseEventBytes.Bit(MouseInjectPlan.Button.Middle));
        Assert.Equal(8, MouseEventBytes.Bit(MouseInjectPlan.Button.Forward));
        Assert.Equal(16, MouseEventBytes.Bit(MouseInjectPlan.Button.Back));
    }

    [Fact]
    public void Mouse_state_event_matches_the_mouse_state_layout()
    {
        var e = MouseEventBytes.MouseStateEvent(deviceId: 7, time: 3.25, x: 640.5f, y: 120f, buttons: 3, clickCount: 1);
        Assert.Equal(56, e.Length);
        Assert.Equal(KeyEventBytes.StateType, BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x0)));    // 'STAT'
        Assert.Equal(54, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x4)));                       // 0x18 + 30
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x6)));
        Assert.Equal(3.25, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(0x8))));
        Assert.Equal(new byte[] { (byte)'S', (byte)'U', (byte)'O', (byte)'M' }, e.Skip(0x14).Take(4).ToArray());
        Assert.Equal(640.5f, BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(0x18)));   // position.x
        Assert.Equal(120f, BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(0x1C)));     // position.y
        Assert.True(e.Skip(0x20).Take(16).All(b => b == 0));                              // delta, scroll
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x18 + 0x18))); // buttons
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x18 + 0x1C))); // clickCount
    }

    [Fact]
    public void Mouse_op_refuses_on_the_test_side_after_parsing()
    {
        using var doc = JsonDocument.Parse("{\"action\":\"click\",\"x\":1,\"y\":1}");
        var e = Assert.Throws<InvalidOperationException>(() => MouseInjectOps.Run(doc.RootElement.Clone()));
        Assert.Equal(MouseInjectOps.ClientOnlyMessage, e.Message);
    }
}
