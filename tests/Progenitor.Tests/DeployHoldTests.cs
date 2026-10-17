using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using Boot = Progenitor.Bootstrap;

namespace Progenitor.Tests;

/// <summary>
/// Deploy-hold defect (2026-10-06). <c>tools/deploy.ps1 -Projects Progenitor.Core,Progenitor.Dev
/// -Side client</c> ran while the client hosted a private game. The watcher refused the staged Core
/// ("Core watch refused: this client is hosting a private game; ...") without parking it or
/// recording a lastReload, the hold saw nothing it recognized, and on its timeout it copied
/// Progenitor.Dev.dll anyway. The new Dev loaded against the old Core, threw TypeLoadException,
/// and its bridge gate stayed busy until restart.
///
/// These tests run the production PowerShell functions (lifted out of deploy.ps1 by brace
/// matching) against stubbed bridge answers, so the rule "only a swap that landed releases the
/// plugins" is executed, not grepped. Skipped when no PowerShell is on PATH.
/// </summary>
public class DeployHoldTests
{
    private static readonly string[] WaitFunctions =
    {
        "Invoke-HeldPluginCopy", "Resolve-HeldPlugins", "Complete-CoreSwapWait", "Step-CoreSwapWait", "Test-SwapIsThisRun",
    };

    /// <summary>
    /// The wire names deploy reads (reload.watch.hostingPrivateGame / hostedChild) are the names
    /// Bootstrap serializes. Read-only: CoreReloadStatus is process-wide and other classes set it.
    /// </summary>
    [Fact]
    public void Bootstrap_puts_the_hosted_game_flags_on_the_reload_dump()
    {
        string json = JsonSerializer.Serialize(Boot.CoreReloadStatus.ToDump(3, null, 1));
        using var doc = JsonDocument.Parse(json);
        var watch = doc.RootElement.GetProperty("watch");
        Assert.True(watch.TryGetProperty("hostingPrivateGame", out var hosting));
        Assert.True(hosting.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(watch.TryGetProperty("hostedChild", out _));
        Assert.True(watch.TryGetProperty("pendingNext", out _));
        string deploy = DeployScript();
        Assert.Contains("$reload.watch.hostingPrivateGame", deploy, StringComparison.Ordinal);
        Assert.Contains("$reload.watch.hostedChild", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_script_parses()
    {
        string? shell = PowerShellExe();
        if (shell == null) return;
        string script =
            "$e = $null; $tk = $null\n" +
            "[System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$tk, [ref]$e) | Out-Null\n" +
            "\"errors=\" + $e.Count\n" +
            "$e | ForEach-Object { $_.ToString() }\n";
        string output = RunPowerShell(shell, script, Path.Combine(FindRepoRoot(), "tools", "deploy.ps1"));
        Assert.Contains("errors=0", output, StringComparison.Ordinal);
    }

    /// <summary>The other scripts that know the single-folder layout parse too.</summary>
    [Theory]
    [InlineData("package.ps1")]
    [InlineData("test-server.ps1")]
    [InlineData("stage-test-server.ps1")]
    [InlineData("launch-client.ps1")]
    [InlineData("verify.ps1")]
    [InlineData("headless-root.ps1")]
    [InlineData("resilience-report.ps1")]
    public void Layout_scripts_parse(string file)
    {
        string? shell = PowerShellExe();
        if (shell == null) return;
        string script =
            "$e = $null; $tk = $null\n" +
            "[System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$tk, [ref]$e) | Out-Null\n" +
            "\"errors=\" + $e.Count\n" +
            "$e | ForEach-Object { $_.ToString() }\n";
        string output = RunPowerShell(shell, script, Path.Combine(FindRepoRoot(), "tools", file));
        Assert.Contains("errors=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_CoreStatus_reads_the_hosted_game_flags()
    {
        string? shell = PowerShellExe();
        if (shell == null) return;
        string fn = ExtractPowerShellFunction(DeployScript(), "Get-CoreStatus");
        string script =
            "function Assert-NotLiveServerPath($p) { }\n" +
            "function node { $global:LASTEXITCODE = 0; '{\"reload\":{\"generation\":7,\"inFlight\":false,\"previousAlive\":false," +
            "\"watch\":{\"enabled\":true,\"pendingNext\":true,\"hostingPrivateGame\":true,\"hostedChild\":false,\"parked\":false}," +
            "\"lastReload\":{\"gen\":7,\"outcome\":\"ok\",\"reason\":\"\",\"timestamp\":\"\"}}}' }\n" +
            fn + "\n" +
            "$st = Get-CoreStatus @{ Name = 'client'; Config = $PSScriptRoot } 3\n" +
            "\"ok=$($st.Ok) hosting=$($st.HostingPrivateGame) child=$($st.HostedChild) gen=$($st.Generation) pending=$($st.PendingNext)\"\n";
        string output = RunPowerShell(shell, script);
        Assert.Contains("ok=True hosting=True child=False gen=7 pending=True", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FINJ1: reload.processLifetimePlugins is read, and a deployed DLL the running process keeps
    /// (it registers its own IL2CPP types) is found by file name, case-insensitively.
    /// </summary>
    [Fact]
    public void Get_CoreStatus_reads_process_lifetime_plugins_and_the_copy_finds_them()
    {
        string? shell = PowerShellExe();
        if (shell == null) return;
        string deploy = DeployScript();
        string script =
            "function Assert-NotLiveServerPath($p) { }\n" +
            "$plan = $false; $script:CoreSwapPollTimeout = 3\n" +
            "function node { $global:LASTEXITCODE = 0; '{\"reload\":{\"generation\":7,\"inFlight\":false,\"previousAlive\":false," +
            "\"watch\":{\"enabled\":true,\"pendingNext\":false,\"hostingPrivateGame\":false,\"hostedChild\":false,\"parked\":false}," +
            "\"lastReload\":{\"gen\":7,\"outcome\":\"ok\",\"reason\":\"\",\"timestamp\":\"\"}," +
            "\"processLifetimePlugins\":[{\"file\":\"ChatSkin.dll\",\"guids\":[\"chatskin\"],\"coreBound\":false,\"restartPending\":\"\"}," +
            "{\"file\":\"Bound.dll\",\"guids\":[\"bound\"],\"coreBound\":true,\"restartPending\":\"changed\"}]}}' }\n" +
            ExtractPowerShellFunction(deploy, "Get-CoreStatus") + "\n" +
            ExtractPowerShellFunction(deploy, "Get-ProcessLifetimeFile") + "\n" +
            "$t = @{ Name = 'client'; Config = $PSScriptRoot; Running = $true; LifetimeStatus = $null }\n" +
            "$st = Get-CoreStatus $t 3\n" +
            "\"count=$($st.ProcessLifetime.Count) bound=$(@($st.ProcessLifetime | Where-Object { $_.CoreBound }).Count)\"\n" +
            "\"chat=$((Get-ProcessLifetimeFile $t 'chatskin.DLL').File) other=[$(Get-ProcessLifetimeFile $t 'Other.dll')]\"\n";
        string output = RunPowerShell(shell, script);
        Assert.Contains("count=2 bound=1", output, StringComparison.Ordinal);
        Assert.Contains("chat=ChatSkin.dll other=[]", output, StringComparison.Ordinal);
    }

    /// <summary>Job FINJ1: a Core-bound process-lifetime plugin withholds before the hold is armed.</summary>
    [Fact]
    public void A_core_bound_process_lifetime_plugin_withholds_the_plugins_before_staging()
    {
        string s = DeployScript();
        int bound = s.IndexOf("$bound = @($before.ProcessLifetime | Where-Object { $_.CoreBound })", StringComparison.Ordinal);
        int arm = s.IndexOf("$t.HoldPlugins = $true", StringComparison.Ordinal);
        Assert.True(bound > 0 && arm > bound, "the pre-check must read processLifetimePlugins before arming the hold");
        Assert.Contains("$t.WithholdReason = \"a process-lifetime plugin", s, StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(Boot.CoreReloadStatus.ToDump(3, null, 1));
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("processLifetimePlugins", out _));
    }

    /// <summary>The live defect: a hosted private game. The wait ends at once and nothing is copied.</summary>
    [Fact]
    public void A_hosted_private_game_withholds_the_plugins_instead_of_copying_them()
    {
        string? output = RunWaitStep("hosting", expired: false, release: false);
        if (output == null) return;
        Assert.Contains("done=True swapped=False withheld=1 copied=0", output, StringComparison.Ordinal);
        Assert.Contains("hosted private game", output, StringComparison.Ordinal);
        Assert.Contains("WITHHELD", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hosted_child_withholds_the_plugins()
    {
        string? output = RunWaitStep("child", expired: false, release: false);
        if (output == null) return;
        Assert.Contains("done=True swapped=False withheld=1 copied=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_swap_withholds_the_plugins()
    {
        string? output = RunWaitStep("refused", expired: false, release: false);
        if (output == null) return;
        Assert.Contains("done=True swapped=False withheld=1 copied=0", output, StringComparison.Ordinal);
    }

    /// <summary>The deadline used to copy anyway; it withholds now.</summary>
    [Fact]
    public void The_deadline_withholds_the_plugins()
    {
        string? output = RunWaitStep("pending", expired: true, release: false);
        if (output == null) return;
        Assert.Contains("done=True swapped=False withheld=1 copied=0", output, StringComparison.Ordinal);
        Assert.Contains("not observed within the window", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_swap_inside_the_window_keeps_waiting()
    {
        string? output = RunWaitStep("pending", expired: false, release: false);
        if (output == null) return;
        Assert.Contains("done=False swapped=False withheld=0 copied=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_swap_that_landed_releases_the_plugins()
    {
        string? output = RunWaitStep("swapped", expired: false, release: false);
        if (output == null) return;
        Assert.Contains("done=True swapped=True withheld=0 copied=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseHeldPlugins_restores_the_copy_anyway_order()
    {
        string? output = RunWaitStep("hosting", expired: false, release: true);
        if (output == null) return;
        Assert.Contains("done=True swapped=False withheld=0 copied=1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Source pins for the parts the executed tests cannot reach without a game: the pre-check
    /// reads the hosted flags before it arms the hold, the `finally` resolves instead of copying,
    /// and a withheld run exits non-zero.
    /// </summary>
    [Fact]
    public void The_precheck_and_the_exit_code_follow_the_withhold_rule()
    {
        string s = DeployScript();
        int hosted = s.IndexOf("if ($before.HostingPrivateGame -or $before.HostedChild)", StringComparison.Ordinal);
        int arm = s.IndexOf("$t.HoldPlugins = $true", StringComparison.Ordinal);
        Assert.True(hosted > 0 && arm > hosted, "the pre-check must read the hosted flags before arming the hold");
        Assert.DoesNotContain("foreach ($t in $heldSides) { Invoke-HeldPluginCopy $t }", s, StringComparison.Ordinal);
        Assert.Contains("exit 3", s, StringComparison.Ordinal);
        Assert.Contains("if ($t.HoldPlugins -or $t.WithholdReason)", s, StringComparison.Ordinal);
    }

    private static string? RunWaitStep(string scenario, bool expired, bool release)
    {
        string? shell = PowerShellExe();
        if (shell == null) return null;
        string deploy = DeployScript();
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'\n");
        sb.Append("$CoreSwapPollSeconds = 5; $CoreSwapTimeoutSeconds = 90\n");
        sb.Append("$script:CoreSwapPollTimeout = 3; $script:CoreSwapSilence = 30; $script:CoreSwapParkConfirm = 2\n");
        sb.Append("$script:MissingMethodNote = 'note'; $script:WithheldNextStep = 'next step'\n");
        sb.Append("$script:Withheld = New-Object System.Collections.Generic.List[object]\n");
        sb.Append("$ReleaseHeldPlugins = [bool]::Parse($args[1])\n");
        sb.Append("$script:Copied = New-Object System.Collections.Generic.List[string]\n");
        sb.Append("function Copy-PluginToReload($t, $dll, $proj) { $script:Copied.Add($dll.Name) }\n");
        sb.Append("$st = [pscustomobject]@{ Ok = $true; Generation = 5; InFlight = $false; WatchEnabled = $true; PendingNext = $true; " +
                  "WatchParked = $false; WatchParkedReason = ''; HostingPrivateGame = $false; HostedChild = $false; WatchStampUtc = $null; " +
                  "PreviousAlive = $false; LastOutcome = 'ok'; LastReason = ''; LastTimestampUtc = $null; PinDiag = ''; Error = '' }\n");
        sb.Append("switch ($args[3]) {\n");
        sb.Append("  'hosting' { $st.HostingPrivateGame = $true }\n");
        sb.Append("  'child' { $st.HostedChild = $true }\n");
        sb.Append("  'refused' { $st.PreviousAlive = $true; $st.LastOutcome = 'refused' }\n");
        sb.Append("  'swapped' { $st.Generation = 6; $st.PendingNext = $false; $st.LastTimestampUtc = (Get-Date).ToUniversalTime() }\n");
        sb.Append("}\n");
        sb.Append("$script:Status = $st\n");
        sb.Append("function Get-CoreStatus($t, $TimeoutSeconds) { return $script:Status }\n");
        foreach (string name in WaitFunctions)
            sb.Append(ExtractPowerShellFunction(deploy, name)).Append('\n');
        sb.Append("$core = $args[0]\n");
        sb.Append("$t = @{ Name = 'client'; Core = $core; Deferred = (New-Object System.Collections.Generic.List[object]); " +
                  "GenerationBefore = 5; StagedAtUtc = (Get-Date).ToUniversalTime().AddSeconds(-10); HeldResolved = $false; HeldCopied = $false }\n");
        sb.Append("$t.Deferred.Add(@{ Dll = [pscustomobject]@{ Name = 'Progenitor.Dev.dll' }; Proj = 'Progenitor.Dev' }) | Out-Null\n");
        sb.Append("$s = [pscustomobject]@{ T = $t; Name = 'client'; NextFile = (Join-Path $core 'Progenitor.dll.next'); FailedBefore = @(); " +
                  "Started = (Get-Date); LastPoll = [datetime]::MinValue; LastAnswer = (Get-Date); LastError = ''; LastStatus = $null; " +
                  "Polls = 0; Misses = 0; ConsecutiveMisses = 0; ParkFirstSeen = $null; ParkPolls = 0; Done = $false; Swapped = $false }\n");
        sb.Append("Step-CoreSwapWait $s ($args[2] -eq 'expired') 3>&1 | ForEach-Object { 'WARN ' + $_ }\n");
        // Resolving twice must not record or copy twice (the wait and the caller's finally both reach it).
        sb.Append("if ($s.Done) { Resolve-HeldPlugins $t $false 'again' 3>$null }\n");
        sb.Append("\"RESULT done=$($s.Done) swapped=$($s.Swapped) withheld=$($script:Withheld.Count) copied=$($script:Copied.Count) " +
                  "reason=$(@($script:Withheld | ForEach-Object { $_.Reason }) -join '|')\"\n");

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-deployhold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Progenitor.dll.next"), "staged");
            return RunPowerShell(shell, sb.ToString(), dir, release ? "true" : "false", expired ? "expired" : "live", scenario);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private static string DeployScript() => File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "deploy.ps1"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Lifts one <c>function Name { … }</c> out of a script by brace matching.</summary>
    private static string ExtractPowerShellFunction(string script, string name)
    {
        int start = script.IndexOf("function " + name + "(", StringComparison.Ordinal);
        if (start < 0) start = script.IndexOf("function " + name + " ", StringComparison.Ordinal);
        Assert.True(start >= 0, "source pin: deploy.ps1 no longer defines " + name);
        int open = script.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < script.Length; i++)
        {
            if (script[i] == '{') depth++;
            else if (script[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return script.Substring(start, i - start + 1);
            }
        }
        throw new InvalidOperationException("unbalanced braces in " + name);
    }

    /// <summary>Runs a script file from a fresh temp folder that also holds a dummy mcp/call.mjs.</summary>
    private static string RunPowerShell(string shell, string script, params string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-deployhold-ps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "mcp"));
        File.WriteAllText(Path.Combine(dir, "mcp", "call.mjs"), "// stub");
        string file = Path.Combine(dir, "harness.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo(shell)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(file);
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            Assert.NotNull(p);
            var stderrTask = p!.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = stderrTask.Result;
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, shell + " exited " + p.ExitCode + ": " + stderr + "\n" + stdout);
            return stdout;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private static bool _shellChecked;
    private static string? _shell;

    private static string? PowerShellExe()
    {
        if (_shellChecked) return _shell;
        _shellChecked = true;
        foreach (string exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(30_000);
                if (p.ExitCode == 0) { _shell = exe; return _shell; }
            }
            catch
            {
                // not on PATH
            }
        }
        return null;
    }
}
