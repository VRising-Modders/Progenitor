using System;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ConnectAddressParser is the Direct-Connect address split: IPv4 host + port, no game types.
/// ConnectOps feeds the result to CreateSteamIPv4 or CreateLidgrenIPv4 (args.protocol);
/// these tests cover the accepted forms and the refusals (hostname, IPv6, bad scheme,
/// including Lidgren:// — pick the transport with args.protocol, not an address scheme)
/// without LaunchGame.
/// </summary>
public class ConnectAddressParserTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", 9976)]
    [InlineData("  127.0.0.1  ", "127.0.0.1", 9976)]
    [InlineData("0.0.0.0", "0.0.0.0", 9976)]
    [InlineData("255.255.255.255", "255.255.255.255", 9976)]
    public void Host_only_uses_the_default_port(string input, string host, int port)
    {
        var parsed = ConnectAddressParser.Parse(input);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("127.0.0.1:27015", "127.0.0.1", 27015)]
    [InlineData("127.0.0.1:1", "127.0.0.1", 1)]
    [InlineData("127.0.0.1:65535", "127.0.0.1", 65535)]
    [InlineData("  10.0.0.2 : 9976  ", "10.0.0.2", 9976)]
    public void Host_colon_port_splits_the_port(string input, string host, int port)
    {
        var parsed = ConnectAddressParser.Parse(input);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("SteamIPv4://127.0.0.1:9976", "127.0.0.1", 9976)]
    [InlineData("steamipv4://127.0.0.1:27015", "127.0.0.1", 27015)]
    [InlineData("STEAMIPV4://10.0.0.1:1", "10.0.0.1", 1)]
    [InlineData("SteamIPv4://127.0.0.1", "127.0.0.1", 9976)]
    [InlineData("SteamIPv4:// 127.0.0.1:9977 ", "127.0.0.1", 9977)]
    public void SteamIPv4_scheme_is_stripped(string input, string host, int port)
    {
        var parsed = ConnectAddressParser.Parse(input);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Fact]
    public void Explicit_port_wins_over_a_port_in_the_address()
    {
        var parsed = ConnectAddressParser.Parse("127.0.0.1:27015", explicitPort: 9976);
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(9976, parsed.Port);
    }

    [Fact]
    public void Explicit_port_wins_over_a_SteamIPv4_port()
    {
        var parsed = ConnectAddressParser.Parse("SteamIPv4://127.0.0.1:27015", explicitPort: 1234);
        Assert.Equal(1234, parsed.Port);
    }

    [Fact]
    public void Explicit_port_is_used_when_the_address_has_no_port()
    {
        var parsed = ConnectAddressParser.Parse("127.0.0.1", explicitPort: 27015);
        Assert.Equal(27015, parsed.Port);
    }

    [Fact]
    public void Custom_default_port_is_used_when_neither_is_set()
    {
        var parsed = ConnectAddressParser.Parse("127.0.0.1", explicitPort: null, defaultPort: 9876);
        Assert.Equal(9876, parsed.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_address_is_required(string? input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:9976")]
    [InlineData("example.com")]
    [InlineData("example.com:9976")]
    [InlineData("SteamIPv4://localhost:9976")]
    public void Hostnames_are_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("hostnames are not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]:9976")]
    [InlineData("2001:db8::1")]
    [InlineData("SteamIPv4://[::1]:9976")]
    [InlineData("::ffff:127.0.0.1")]
    public void IPv6_is_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("IPv6", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Lidgren://127.0.0.1:9976")]
    [InlineData("http://127.0.0.1:9976")]
    [InlineData("SteamP2P://127.0.0.1:9976")]
    public void Unsupported_scheme_is_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("unsupported address scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:abc")]
    [InlineData("SteamIPv4://127.0.0.1:nope")]
    public void Non_integer_address_port_is_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:-1")]
    public void Out_of_range_address_port_is_rejected(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse(input));
        Assert.Contains("1–65535", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_of_range_explicit_port_is_rejected(int port)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse("127.0.0.1", explicitPort: port));
        Assert.Contains("1–65535", ex.Message);
    }

    [Fact]
    public void Explicit_port_can_rescue_an_out_of_range_address_port()
    {
        var parsed = ConnectAddressParser.Parse("127.0.0.1:99999", explicitPort: 9976);
        Assert.Equal(9976, parsed.Port);
    }

    [Fact]
    public void SteamIPv4_with_empty_host_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectAddressParser.Parse("SteamIPv4://"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultPort_is_the_test_server_game_port()
    {
        Assert.Equal(9976, ConnectAddressParser.DefaultPort);
    }
}
