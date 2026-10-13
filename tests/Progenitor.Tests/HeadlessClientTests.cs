using Progenitor;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

public class HeadlessClientTests
{
    [Fact]
    public void Shim_installs_only_for_a_tagged_headless_batchmode_client()
    {
        Assert.True(HeadlessClientPolicy.ShouldInstall(false, "client-1", "1", true));
        Assert.False(HeadlessClientPolicy.ShouldInstall(true, "client-1", "1", true));   // server
        Assert.False(HeadlessClientPolicy.ShouldInstall(false, "", "1", true));          // primary client
        Assert.False(HeadlessClientPolicy.ShouldInstall(false, null, "1", true));
        Assert.False(HeadlessClientPolicy.ShouldInstall(false, "client-1", null, true)); // not asked
        Assert.False(HeadlessClientPolicy.ShouldInstall(false, "client-1", "true", true));
        Assert.False(HeadlessClientPolicy.ShouldInstall(false, "client-1", "1", false)); // has a window
    }

    [Fact]
    public void Batchmode_arg_is_matched_case_insensitively_as_a_whole_argument()
    {
        Assert.True(HeadlessClientPolicy.HasBatchModeArg(new[] { "VRising.exe", "-batchMode" }));
        Assert.False(HeadlessClientPolicy.HasBatchModeArg(new[] { "VRising.exe", "-batchmodex", "-logFile" }));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.4.5.6", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not an ip", false)]
    public void Band_ids_are_admitted_only_from_loopback(string? address, bool expected)
    {
        Assert.Equal(expected, ExtraInstanceServerAuth.IsLoopbackAddressText(address));
    }

    [Theory]
    [InlineData(0, 9986, 9988)]
    [InlineData(9990, 9986, 9990)]
    [InlineData(9986, 9986, 9988)]
    [InlineData(-1, 9876, 9878)]
    [InlineData(70000, 9876, 9878)]
    [InlineData(0, 65534, 0)]
    public void Loopback_port_is_configured_or_game_port_plus_two(int configured, int game, int expected)
    {
        Assert.Equal(expected, LoopbackTransport.ChoosePort(configured, game));
    }
}
