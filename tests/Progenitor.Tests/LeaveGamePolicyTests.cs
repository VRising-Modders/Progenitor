using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

public sealed class LeaveGamePolicyTests
{
    [Fact]
    public void Status_never_requests_and_repeated_request_is_pending()
    {
        var p = new LeaveGamePolicy();
        Assert.Equal("inGame", p.Observe(true, 1, false, true, false, 0));
        Assert.False(p.Requested);
        Assert.Equal("request", p.Observe(true, 1, false, false, false, 1));
        Assert.True(p.Requested);
        Assert.Equal("pending", p.Observe(true, 1, false, false, false, 2));
    }
    [Fact]
    public void World_disappearance_is_not_menu_verification()
    {
        var p = new LeaveGamePolicy();
        p.Observe(true, 1, false, false, false, 0);
        Assert.Equal("pending", p.Observe(false, 0, false, true, false, 1));
        Assert.Equal("verified", p.Observe(false, 0, true, true, false, 2));
        Assert.False(p.Requested);
    }
    [Fact]
    public void Changed_world_cannot_receive_old_request_on_poll()
    {
        var p = new LeaveGamePolicy();
        p.Observe(true, 1, false, false, false, 0);
        Assert.Equal("superseded", p.Observe(true, 2, false, false, false, 1));
        Assert.False(p.Requested);
        Assert.Equal("inGame", p.Observe(true, 2, false, true, false, 2));
    }
    [Fact]
    public void Timeout_does_not_automatically_resend()
    {
        var p = new LeaveGamePolicy();
        p.Observe(true, 1, false, false, false, 0);
        Assert.Equal("timedOut", p.Observe(true, 1, false, false, false, 121));
        Assert.True(p.Requested);
        Assert.Equal("timedOut", p.Observe(true, 1, false, false, false, 122));
    }
    [Fact]
    public void Partial_native_failure_is_retained_until_observed_completion()
    {
        var p = new LeaveGamePolicy();
        p.Observe(true, 1, false, false, false, 0);
        p.Fail("partial transition");
        Assert.Equal("requestFailed", p.Observe(true, 1, false, false, false, 1));
        Assert.Equal("partial transition", p.Failure);
        Assert.Equal("verified", p.Observe(false, 0, true, true, false, 2));
        Assert.Null(p.Failure);
    }
    [Fact]
    public void Loading_and_absent_world_do_not_initiate_transition()
    {
        var p = new LeaveGamePolicy();
        Assert.Equal("launchInFlight", p.Observe(true, 1, false, false, true, 0));
        Assert.Equal("notVerified", p.Observe(false, 0, false, false, false, 0));
        Assert.False(p.Requested);
    }
}
