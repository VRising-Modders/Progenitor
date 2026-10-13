using System;
using Progenitor.Bootstrap;
using Progenitor.Native;
using Xunit;

namespace Progenitor.Tests;

[Collection("NativePinRegistry")]
public class KeyTableProbeTests : IDisposable
{
    public KeyTableProbeTests()
    {
        NativePinRegistry.ResetForTests();
        KeyTableProbe.Uninstall();
    }

    public void Dispose()
    {
        KeyTableProbe.Uninstall();
        NativePinRegistry.ResetForTests();
    }

    [Fact]
    public void Uninstall_leaves_no_probe()
    {
        Assert.Equal("keyprobe.findentry", KeyTableProbe.PinName);
        Assert.False(KeyTableProbe.IsInstalled);

        var handle = KeyTableProbe.Install(1, 2);
        Assert.NotNull(handle);
        Assert.True(KeyTableProbe.IsInstalled);
        Assert.Contains(NativePinRegistry.Describe(), line => line.Contains(KeyTableProbe.PinName, StringComparison.Ordinal));

        Assert.Null(KeyTableProbe.Install(3, 4));
        Assert.True(KeyTableProbe.IsInstalled);

        KeyTableProbe.Uninstall();
        Assert.False(KeyTableProbe.IsInstalled);
        Assert.DoesNotContain(NativePinRegistry.Describe(), line => line.Contains(KeyTableProbe.PinName, StringComparison.Ordinal));

        var again = KeyTableProbe.Install(1, 2);
        Assert.NotNull(again);
        Assert.True(KeyTableProbe.IsInstalled);

        handle!.Dispose();
        Assert.True(KeyTableProbe.IsInstalled);

        again!.Dispose();
        Assert.False(KeyTableProbe.IsInstalled);
        Assert.DoesNotContain(NativePinRegistry.Describe(), line => line.Contains(KeyTableProbe.PinName, StringComparison.Ordinal));
    }
}
