using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public class RelaunchSteamWaitTests
{
    // A non-ASCII install path with a space, a single quote and parentheses.
    private const string UnicodeExe = "D:\\\u0418\u0433\u0440\u044b\\V Rising's (\u00e9t\u00e9)\\VRising.exe";

    [Theory]
    [InlineData(60, false, 0, "LAUNCH:60")]
    [InlineData(0, false, 0, "LAUNCH:8")]
    [InlineData(999, false, 4, "")]
    [InlineData(0, true, 4, "")]
    public void Generated_helper_waits_for_explicit_steam_idle(int clearsAt, bool unreadable,
        int expectedExit, string expectedOutput)
    {
        if (!OperatingSystem.IsWindows()) return; // Executes the inbox Windows PowerShell helper.
        var (exit, output, error) = Run(Harness(clearsAt, unreadable, @"C:\Fake\VRising.exe")
            + RelaunchCommand.Script(4242, 1, @"C:\Fake\VRising.exe"));
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOutput, output);
        Assert.True(string.IsNullOrEmpty(error), error);
    }

    [Fact]
    public void Helper_matches_a_non_ascii_game_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The fake game reports its path built from character codes, independent of how the script travels, so the
        // helper's own copy of the path must arrive intact for its Up check to see the game (one request, no re-ask).
        var (exit, output, error) = Run(Harness(0, false, UnicodeExe) + RelaunchCommand.Script(4242, 1, UnicodeExe));
        Assert.Equal(0, exit);
        Assert.Equal("LAUNCH:8", output);
        Assert.True(string.IsNullOrEmpty(error), error);
    }

    [Fact]
    public void Broker_passes_a_non_ascii_helper_command_line_to_windows_intact()
    {
        if (!OperatingSystem.IsWindows()) return;
        string ps = RelaunchCommand.PowerShellPath(Environment.SystemDirectory);
        string helperArgs = RelaunchCommand.Arguments(RelaunchCommand.Script(4242, 1, UnicodeExe,
            logPath: Path.Combine(Path.GetDirectoryName(UnicodeExe)!, "BepInEx", "progenitor-restart.log")));
        string broker = RelaunchCommand.BrokerScript(ps, helperArgs);
        Assert.DoesNotContain("\"", broker, StringComparison.Ordinal);

        // Stub the Windows process creation: report the exact command line as UTF-16 base64, then a fake id.
        string harness =
            "function New-CimInstance{param($ClassName,[switch]$ClientOnly,$Property)'startup'};" +
            "function Invoke-CimMethod{param($ClassName,$MethodName,$Arguments)" +
            "[Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Arguments.CommandLine)));" +
            "[pscustomobject]@{ReturnValue=0;ProcessId=777}};";
        var (exit, output, error) = Run(harness + broker);
        Assert.True(exit == 0, error);
        var lines = output.Split('\n').Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        string received = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(lines[0]));
        Assert.Equal("\"" + ps + "\" " + helperArgs, received);
        Assert.Contains(UnicodeExe.Replace("'", "''"), received, StringComparison.Ordinal);
        Assert.Equal("777", lines[1]);
    }

    [Fact]
    public void Helper_logs_its_outcome_and_keeps_the_log_short()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-restart-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "progenitor-restart.log");
            File.WriteAllLines(log, Enumerable.Range(1, 300).Select(i => "old line " + i));
            var (exit, _, error) = Run(Harness(999, false, @"C:\Fake\VRising.exe")
                + RelaunchCommand.Script(4242, 1, @"C:\Fake\VRising.exe", logPath: log));
            Assert.Equal(4, exit);
            Assert.True(string.IsNullOrEmpty(error), error);
            var lines = File.ReadAllLines(log);
            Assert.True(lines.Length <= RelaunchCommand.LogMaxLines, "log has " + lines.Length + " lines");
            Assert.Contains("old line 300", lines);
            Assert.DoesNotContain("old line 1", lines);
            Assert.Contains("helper started", lines[^3], StringComparison.Ordinal);
            Assert.Contains("nothing started (exit 4)", lines[^1], StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp */ }
        }
    }

    // Stub OS boundaries, not the generated control flow. Virtual time avoids real sleeps; Start-Process only
    // records a request and makes the fake game visible at a path built from character codes.
    private static string Harness(int clearsAt, bool unreadable, string gamePath) =>
        "$script:clock=0;$script:started=$false;" +
        "$script:game=[string]::new([char[]]@(" + string.Join(",", gamePath.Select(c => ((int)c).ToString())) + "));" +
        "function Get-Date{[datetime]::new(2026,1,1).AddSeconds($script:clock)};" +
        "function Start-Sleep{param($Seconds)$script:clock+=$Seconds};" +
        "function Get-Process{param($Id)if($script:started){[pscustomobject]@{Path=$script:game}}};" +
        "function Get-ItemProperty{param($Path,$Name,$ErrorAction)" +
        (unreadable ? "throw 'unavailable'" :
            "[pscustomobject]@{Running=[int]($script:clock -lt " + clearsAt + ")}") + "};" +
        "function Start-Process{param($FilePath)$script:started=$true;Write-Output ('LAUNCH:'+$script:clock)};";

    private static (int Exit, string Output, string Error) Run(string script)
    {
        var info = new ProcessStartInfo(RelaunchCommand.PowerShellPath(Environment.SystemDirectory),
            RelaunchCommand.Arguments(script))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(15000), "Simulated restart script did not finish.");
        process.WaitForExit();
        return (process.ExitCode, stdout.Result.Trim(), stderr.Result);
    }
}
