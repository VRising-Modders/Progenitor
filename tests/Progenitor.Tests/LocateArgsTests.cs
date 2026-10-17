using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The Dev bridge's <c>locate</c> op arguments (game-free half).</summary>
public sealed class LocateArgsTests
{
    private static LocateRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LocateArgs.Parse(doc.RootElement.Clone());
    }

    [Fact]
    public void Status_is_the_default()
    {
        Assert.Equal("status", Parse("{}").Action);
        Assert.Equal("clear", Parse("{\"action\":\"clear\"}").Action);
    }

    [Fact]
    public void Pin_takes_the_world_point_and_its_options()
    {
        var r = Parse("{\"action\":\"pin\",\"x\":-1444,\"y\":5.02,\"z\":-740,\"label\":\"Minerals\",\"seconds\":60,\"arrival\":3,\"maps\":true}");
        Assert.Equal(-1444f, r.X);
        Assert.Equal(5.02f, r.Y);
        Assert.Equal(-740f, r.Z);
        Assert.Equal("Minerals", r.Label);
        Assert.Equal(60, r.Seconds);
        Assert.Equal(3f, r.Arrival);
        Assert.True(r.Maps);

        var d = Parse("{\"action\":\"pin\",\"position\":[1,2,3]}");
        Assert.Equal((1f, 2f, 3f), (d.X, d.Y, d.Z));
        Assert.Equal(LocateArgs.DefaultSeconds, d.Seconds);
        Assert.Equal(0f, d.Arrival);
        Assert.False(d.Maps);
    }

    [Theory]
    [InlineData("{\"action\":\"pin\",\"x\":1,\"y\":2}")]
    [InlineData("{\"action\":\"pin\",\"x\":1,\"y\":2,\"z\":\"3\"}")]
    [InlineData("{\"action\":\"pin\",\"x\":1,\"y\":2,\"z\":3,\"arrival\":-1}")]
    [InlineData("{\"action\":\"pin\",\"x\":1,\"y\":2,\"z\":3,\"seconds\":-1}")]
    [InlineData("{\"action\":\"pin\",\"position\":[1,2]}")]
    [InlineData("{\"action\":\"status\",\"x\":1}")]
    [InlineData("{\"action\":\"move\"}")]
    [InlineData("{\"action\":\"pin\",\"x\":1,\"y\":2,\"z\":3,\"maps\":\"yes\"}")]
    public void Bad_arguments_are_refused(string json)
    {
        Assert.Throws<ArgumentException>(() => Parse(json));
    }
}
