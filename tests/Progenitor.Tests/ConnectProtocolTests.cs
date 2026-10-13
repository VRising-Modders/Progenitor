using System;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ConnectOps.ParseConnectProtocol is the Direct-Connect transport switch: SteamIPv4
/// (default) or Lidgren. The address parser still refuses a Lidgren:// scheme; these
/// tests cover the args.protocol names without LaunchGame.
/// </summary>
public class ConnectProtocolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Omitted_or_empty_is_SteamIPv4(string? input)
    {
        Assert.Equal(ConnectOps.ProtocolSteamIPv4, ConnectOps.ParseConnectProtocol(input));
    }

    [Theory]
    [InlineData("SteamIPv4")]
    [InlineData("steamipv4")]
    [InlineData("STEAMIPV4")]
    [InlineData(" SteamIPv4 ")]
    public void SteamIPv4_is_canonical(string input)
    {
        Assert.Equal("SteamIPv4", ConnectOps.ParseConnectProtocol(input));
    }

    [Theory]
    [InlineData("Lidgren")]
    [InlineData("lidgren")]
    [InlineData("LIDGREN")]
    [InlineData(" Lidgren ")]
    public void Lidgren_is_canonical(string input)
    {
        Assert.Equal("Lidgren", ConnectOps.ParseConnectProtocol(input));
    }

    [Theory]
    [InlineData("LidgrenIPv4")]
    [InlineData("Lidgren://127.0.0.1:9976")]
    [InlineData("Steam")]
    [InlineData("udp")]
    public void Unknown_protocol_is_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectOps.ParseConnectProtocol(input));
        Assert.Contains("SteamIPv4 or Lidgren", ex.Message, StringComparison.Ordinal);
        Assert.Contains(input, ex.Message, StringComparison.Ordinal);
    }
}
