using System;
using System.Diagnostics;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public class RelaunchSteamWaitTests
{
    [Theory]
    [InlineData(60, false, 0, "LAUNCH:60")]
    [InlineData(0, false, 0, "LAUNCH:8")]
    [InlineData(999, false, 4, "")]
    [InlineData(0, true, 4, "")]
    public void Generated_helper_waits_for_explicit_steam_idle(int clearsAt, bool unreadable,
        int expectedExit, string expectedOutput)
    {
        if (!OperatingSystem.IsWindows()) return; // Executes the inbox Windows PowerShell helper.
        // Stub OS boundaries, not the generated control flow. Virtual time avoids real sleeps;
        // Start-Process only records a request and makes the fake game visible.
        string harness = "$script:clock=0;$script:started=$false;" +
            "function Get-Date{[datetime]::new(2026,1,1).AddSeconds($script:clock)};" +
            "function Start-Sleep{param($Seconds)$script:clock+=$Seconds};" +
            "function Get-Process{param($Id)if($script:started){[pscustomobject]@{Path='C:\\Fake\\VRising.exe'}}};" +
            "function Get-ItemProperty{param($Path,$Name,$ErrorAction)" +
            (unreadable ? "throw 'unavailable'" :
                "[pscustomobject]@{Running=[int]($script:clock -lt " + clearsAt + ")}") + "};" +
            "function Start-Process{param($FilePath)$script:started=$true;Write-Output ('LAUNCH:'+$script:clock)};";
        string script = harness + RelaunchCommand.Script(4242, 1, @"C:\Fake\VRising.exe");
        var info = new ProcessStartInfo(RelaunchCommand.PowerShellPath(Environment.SystemDirectory),
            RelaunchCommand.Arguments(script))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(15000), "Simulated restart helper did not finish.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd();
        Assert.Equal(expectedExit, process.ExitCode);
        Assert.Equal(expectedOutput, output);
        Assert.True(string.IsNullOrEmpty(error), error);
    }
}
