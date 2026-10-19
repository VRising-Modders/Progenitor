using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The mod template's Progenitor version check (templates/progenitor-mod/Directory.Build.targets,
/// ModCheckProgenitorVersion). Runs the template's real build files with MSBuild against fake Progenitor
/// checkouts whose tools/version/ProgenitorVersion.targets reports a chosen version, and once against this
/// checkout. Needs the dotnet SDK on PATH (and git for the last test); a missing one is a failure, not a pass.
/// </summary>
public sealed class TemplateVersionCheckTests : IDisposable
{
    private readonly string _root;
    private readonly string _mod;

    public TemplateVersionCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-template-version-" + Guid.NewGuid().ToString("N"));
        _mod = Path.Combine(_root, "mod");
        string template = Path.Combine(RepoRoot(), "templates", "progenitor-mod");
        Directory.CreateDirectory(Path.Combine(_mod, "build"));
        foreach (string f in new[] { "Directory.Build.props", "Directory.Build.targets", Path.Combine("build", "ProgenitorVersionProbe.proj") })
            File.Copy(Path.Combine(template, f), Path.Combine(_mod, f));
        File.WriteAllText(Path.Combine(_mod, "Check.proj"),
            "<Project><Import Project=\"Directory.Build.props\" /><Import Project=\"Directory.Build.targets\" /></Project>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    [Theory]
    [InlineData("2026.10.100")]
    [InlineData("2026.10.119.9")]
    [InlineData("2026.9.200")]
    public void AnOlderCheckoutFailsWithOneErrorNamingBothVersions(string checkout)
    {
        var (exit, output) = Check(FakeRepo(checkout), "-p:ProgenitorMinimumVersion=2026.10.120");
        Assert.NotEqual(0, exit);
        Assert.Contains("This mod's template needs Progenitor 2026.10.120 or newer", output, StringComparison.Ordinal);
        Assert.Contains("is a checkout of " + checkout + ".", output, StringComparison.Ordinal);
        Assert.Contains("dotnet new install", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026.10.120")]
    [InlineData("2026.10.120.3")]
    [InlineData("2026.10.121")]
    [InlineData("2026.11.1")]
    [InlineData("2027.1.0")]
    public void TheSameOrANewerCheckoutPasses(string checkout)
    {
        var (exit, output) = Check(FakeRepo(checkout), "-p:ProgenitorMinimumVersion=2026.10.120");
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("needs Progenitor", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not read", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableVersionWarnsAndContinues()
    {
        // A checkout from before git-derived versions: no tools/version at all.
        var (exit, output) = Check(FakeRepo(null), "-p:ProgenitorMinimumVersion=2026.10.120");
        Assert.True(exit == 0, output);
        Assert.Contains("Could not read the Progenitor version", output, StringComparison.Ordinal);

        // Git unavailable or no main ref: the version targets report 0.0.0.0.
        (exit, output) = Check(FakeRepo("0.0.0.0"), "-p:ProgenitorMinimumVersion=2026.10.120");
        Assert.True(exit == 0, output);
        Assert.Contains("Could not read the Progenitor version", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckCanBeSkipped()
    {
        var (exit, output) = Check(FakeRepo("2026.10.1"), "-p:ProgenitorMinimumVersion=2026.10.120", "-p:ProgenitorSkipVersionCheck=true");
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("needs Progenitor", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCheckoutStillGetsTheNoCheckoutError()
    {
        var (exit, output) = Check(Path.Combine(_root, "nothing-here"));
        Assert.NotEqual(0, exit);
        Assert.Contains("No Progenitor checkout found", output, StringComparison.Ordinal);
        Assert.DoesNotContain("needs Progenitor", output, StringComparison.Ordinal);
    }

    /// <summary>The template's stamp is a valid version and this checkout satisfies it, so the template builds here.</summary>
    [Fact]
    public void TheTemplateStampIsNotNewerThanThisCheckout()
    {
        string props = File.ReadAllText(Path.Combine(RepoRoot(), "templates", "progenitor-mod", "Directory.Build.props"));
        var m = Regex.Match(props, "<ProgenitorMinimumVersion>([^<]*)</ProgenitorMinimumVersion>");
        Assert.True(m.Success, "templates/progenitor-mod/Directory.Build.props has no ProgenitorMinimumVersion");
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", m.Groups[1].Value);

        var (exit, output) = Check(RepoRoot());
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("needs Progenitor", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not read", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>A fake checkout: Core's project file and, unless <paramref name="version"/> is null, version targets
    /// that report <paramref name="version"/>.</summary>
    private string FakeRepo(string? version)
    {
        string repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(repo, "src", "Progenitor.Core"));
        File.WriteAllText(Path.Combine(repo, "src", "Progenitor.Core", "Progenitor.Core.csproj"), "<Project />");
        if (version != null)
        {
            Directory.CreateDirectory(Path.Combine(repo, "tools", "version"));
            File.WriteAllText(Path.Combine(repo, "tools", "version", "ProgenitorVersion.targets"),
                "<Project><Target Name=\"ProgenitorComputeVersion\"><PropertyGroup><ProgenitorVersion>" + version +
                "</ProgenitorVersion></PropertyGroup></Target></Project>");
        }
        return repo;
    }

    private (int Exit, string Output) Check(string progenitorRepo, params string[] extra)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _mod,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // The test host can inherit MSBuild variables from the `dotnet test` that launched it.
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)) psi.Environment.Remove(key);
        }
        psi.Environment.Remove("PROGENITOR_REPO");
        foreach (string a in new[]
        {
            "msbuild", Path.Combine(_mod, "Check.proj"), "-nologo", "-nodeReuse:false",
            "-t:ModCheckProgenitorVersion", "-p:ProgenitorRepo=" + progenitorRepo,
        }) psi.ArgumentList.Add(a);
        foreach (string a in extra) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var err = p!.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(180_000), "dotnet msbuild timed out");
        return (p.ExitCode, stdout + "\n" + err.Result);
    }

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
