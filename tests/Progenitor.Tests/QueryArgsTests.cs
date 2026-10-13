using System;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// QueryArgs is the debug-bridge query policy: refuse an unconstrained scan, stop after
/// <c>limit</c> unless a total is requested, hard-cap the walk. EntityOps.Query applies it
/// against a live world; these tests cover the pure checks.
/// </summary>
public class QueryArgsTests
{
    [Fact]
    public void RequireAllFilter_throws_when_all_is_empty_and_not_opted_in()
    {
        var ex = Assert.Throws<ArgumentException>(() => QueryArgs.RequireAllFilter(0, allowUnbounded: false));
        Assert.Contains("allowUnbounded", ex.Message);
        Assert.Contains("all", ex.Message);
    }

    [Fact]
    public void RequireAllFilter_allows_empty_all_when_unbounded_is_opted_in()
    {
        QueryArgs.RequireAllFilter(0, allowUnbounded: true);
    }

    [Fact]
    public void RequireAllFilter_allows_a_non_empty_all_list()
    {
        QueryArgs.RequireAllFilter(1, allowUnbounded: false);
        QueryArgs.RequireAllFilter(3, allowUnbounded: false);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(50, 50)]
    [InlineData(2000, 2000)]
    [InlineData(2001, 2000)]
    [InlineData(99999, 2000)]
    public void ClampLimit_stays_between_one_and_max(int input, int expected)
    {
        Assert.Equal(expected, QueryArgs.ClampLimit(input));
    }

    [Fact]
    public void Continue_stops_at_row_limit_when_total_is_not_requested()
    {
        var limits = new QueryArgs.Limits(rowLimit: 50, wantTotal: false);
        Assert.True(limits.Continue(visited: 49, accepted: 49));
        Assert.False(limits.Continue(visited: 50, accepted: 50));
        Assert.False(limits.Continue(visited: 10, accepted: 50));
    }

    [Fact]
    public void Continue_keeps_walking_past_row_limit_when_total_is_requested()
    {
        var limits = new QueryArgs.Limits(rowLimit: 50, wantTotal: true);
        Assert.True(limits.Continue(visited: 50, accepted: 50));
        Assert.True(limits.Continue(visited: QueryArgs.HardScanCap - 1, accepted: 4000));
        Assert.False(limits.Continue(visited: QueryArgs.HardScanCap, accepted: 4000));
    }

    [Fact]
    public void Continue_always_stops_at_the_hard_scan_cap()
    {
        var noTotal = new QueryArgs.Limits(rowLimit: 50, wantTotal: false);
        var withTotal = new QueryArgs.Limits(rowLimit: 50, wantTotal: true);
        Assert.False(noTotal.Continue(visited: QueryArgs.HardScanCap, accepted: 0));
        Assert.False(withTotal.Continue(visited: QueryArgs.HardScanCap, accepted: 0));
    }

    [Fact]
    public void IsTruncated_only_when_the_hard_cap_cut_the_match_set_short()
    {
        var limits = new QueryArgs.Limits(rowLimit: 50, wantTotal: true);
        Assert.True(limits.IsTruncated(visited: QueryArgs.HardScanCap, matchCount: QueryArgs.HardScanCap + 1));
        Assert.False(limits.IsTruncated(visited: QueryArgs.HardScanCap, matchCount: QueryArgs.HardScanCap));
        Assert.False(limits.IsTruncated(visited: 50, matchCount: 10_000));
    }

    [Fact]
    public void Stopping_at_row_limit_is_not_truncation()
    {
        var limits = new QueryArgs.Limits(rowLimit: 50, wantTotal: false);
        Assert.False(limits.IsTruncated(visited: 50, matchCount: 5000));
    }

    [Fact]
    public void HardScanCap_is_two_hundred_thousand()
    {
        Assert.Equal(200_000, QueryArgs.HardScanCap);
    }
}
