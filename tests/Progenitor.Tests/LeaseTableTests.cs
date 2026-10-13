using System;
using Progenitor.Config;
using Xunit;

namespace Progenitor.Tests;

public class LeaseTableTests
{
    [Fact]
    public void First_acquire_creates_second_shares_and_returns_a_distinct_lease()
    {
        int creates = 0, destroys = 0;
        var table = new LeaseTable<string, Resource>(StringComparer.OrdinalIgnoreCase);
        var a = table.Acquire("cfg", () => { creates++; return new Resource(); }, _ => destroys++);
        var b = table.Acquire("CFG", () => { creates++; return new Resource(); }, _ => destroys++);

        Assert.NotSame(a, b);
        Assert.Equal(1, creates);
        Assert.Equal(2, table.RefCount("cfg"));
        Assert.Equal(1, table.KeyCount);
        Assert.Equal(0, destroys);

        a.Dispose();
        Assert.Equal(0, destroys);
        Assert.Equal(1, table.RefCount("cfg"));

        b.Dispose();
        Assert.Equal(1, destroys);
        Assert.Equal(0, table.RefCount("cfg"));
        Assert.Equal(0, table.KeyCount);
    }

    [Fact]
    public void Disposing_the_same_lease_twice_destroys_once()
    {
        int destroys = 0;
        var table = new LeaseTable<string, Resource>();
        var lease = table.Acquire("k", () => new Resource(), _ => destroys++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, destroys);
        Assert.Equal(0, table.RefCount("k"));
    }

    [Fact]
    public void Independent_keys_do_not_share_a_count()
    {
        int destroys = 0;
        var table = new LeaseTable<string, Resource>();
        var a = table.Acquire("a", () => new Resource(), _ => destroys++);
        var b = table.Acquire("b", () => new Resource(), _ => destroys++);
        a.Dispose();
        Assert.Equal(1, destroys);
        Assert.Equal(0, table.RefCount("a"));
        Assert.Equal(1, table.RefCount("b"));
        b.Dispose();
        Assert.Equal(2, destroys);
    }

    [Fact]
    public void Drain_destroys_remaining_and_makes_live_leases_no_ops()
    {
        int destroys = 0;
        var table = new LeaseTable<string, Resource>();
        var a = table.Acquire("k", () => new Resource(), _ => destroys++);
        var b = table.Acquire("k", () => new Resource(), _ => destroys++);
        table.Drain();
        Assert.Equal(1, destroys);
        Assert.Equal(0, table.RefCount("k"));
        Assert.Equal(0, table.KeyCount);
        a.Dispose();
        b.Dispose();
        Assert.Equal(1, destroys);
    }

    [Fact]
    public void Acquire_after_last_dispose_creates_again()
    {
        int creates = 0;
        var table = new LeaseTable<string, Resource>();
        var first = table.Acquire("k", () => { creates++; return new Resource(); }, _ => { });
        first.Dispose();
        var second = table.Acquire("k", () => { creates++; return new Resource(); }, _ => { });
        Assert.Equal(2, creates);
        Assert.Equal(1, table.RefCount("k"));
        second.Dispose();
    }

    [Fact]
    public void Create_throw_leaves_the_table_empty()
    {
        var table = new LeaseTable<string, Resource>();
        Assert.Throws<InvalidOperationException>(() =>
            table.Acquire("k", () => throw new InvalidOperationException("nope"), _ => { }));
        Assert.Equal(0, table.RefCount("k"));
        Assert.Equal(0, table.KeyCount);
    }

    private sealed class Resource { }
}
