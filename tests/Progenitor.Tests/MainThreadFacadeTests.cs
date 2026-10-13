using System;
using System.Threading.Tasks;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free check that Core's <see cref="MainThread"/> facade still owns the tagging helpers
/// after the Bootstrap split (the queue itself is not exercised here).
/// </summary>
public class MainThreadFacadeTests
{
    [Fact]
    public void Type_name_stays_Progenitor_Dev_MainThread()
    {
        Assert.Equal("Progenitor.Dev.MainThread", typeof(MainThread).FullName);
    }

    [Fact]
    public void Null_action_Post_throws()
    {
        Assert.Throws<ArgumentNullException>(() => MainThread.Post(null!));
    }

    [Fact]
    public async Task Null_func_Run_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => MainThread.Run<int>(null!));
    }
}
