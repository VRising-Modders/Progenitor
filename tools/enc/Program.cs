// tools/enc — Edit-and-Continue probe (roadmap M4 item 5, job FENC).
//
//   dotnet run --project tools/enc                       # reports Skipped without the env var
//   $env:DOTNET_MODIFIABLE_ASSEMBLIES = "debug"; dotnet run --project tools/enc
//   dotnet run --project tools/enc -- --matrix           # which edits the runtime accepts
//   dotnet run --project tools/enc -- --watch            # job FENC2: the inner dev loop
//
// Exit code 0 for Applied or Skipped, 1 for Failed, so it can run unattended. --watch uses
// 0 for a clean pass, 2 for a rude edit (rebuild and redeploy) and 3 for a setup problem.

using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Enc;

namespace Progenitor.Enc.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--matrix") >= 0)
        {
            Matrix();
            return 0;
        }
        if (Array.IndexOf(args, "--watch") >= 0)
            return EncWatch.Run(WatchOptions(args), Console.WriteLine);

        var result = EncProbe.Run();
        Console.WriteLine(result);
        return result.Status == EncProbeStatus.Failed ? 1 : 0;
    }

    /// <summary>
    /// Watch-mode defaults, all overridable. The Sample is the default plugin because it is the
    /// compliance example and the one the dev loop already deploys; the baseline is its Debug
    /// output, which is what `dotnet build` and `tools/deploy.ps1` produce (a Release build is
    /// refused by the runtime, see EncHotReloadTests).
    ///
    ///   --project &lt;dir&gt;    plugin project folder   (default samples/Progenitor.Sample)
    ///   --baseline &lt;dll&gt;   deployed Debug image     (default that project's bin/Debug/net6.0)
    ///   --out &lt;dir&gt;        reload folder            (default the staged test server's)
    ///   --ref &lt;dir&gt;        extra reference folder   (repeatable; interop dirs are added by default)
    ///   --once             classify and emit one edit, then exit
    /// </summary>
    private static EncWatchOptions WatchOptions(string[] args)
    {
        string repo = FindRepoRoot() ?? Directory.GetCurrentDirectory();
        string project = Value(args, "--project") ?? Path.Combine(repo, "samples", "Progenitor.Sample");
        string plugin = Path.GetFileName(project.TrimEnd('\\', '/'));
        string baseline = Value(args, "--baseline")
            ?? Path.Combine(project, "bin", "Debug", "net6.0", plugin + ".dll");
        // PROGENITOR_TEST_ROOT, else <workspace>\_testserver (the workspace resolver in
        // tools/common/Workspace.ps1 and workspace.mjs, ported below). No machine path is assumed.
        string? testRoot = Environment.GetEnvironmentVariable("PROGENITOR_TEST_ROOT");
        if (string.IsNullOrEmpty(testRoot))
        {
            string? workspace = FindWorkspace(repo);
            testRoot = workspace == null ? null : Path.Combine(workspace, "_testserver");
        }
        string? outArg = Value(args, "--out");
        if (outArg == null && testRoot == null)
            throw new InvalidOperationException(
                "cannot resolve the Progenitor workspace (the folder that holds the main checkout); "
                + "pass --out, or set PROGENITOR_TEST_ROOT or PROGENITOR_WORKSPACE");
        string outDir = outArg ?? Path.Combine(testRoot!, "server", "BepInEx", "progenitor-reload");

        var refs = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--ref") refs.Add(args[i + 1]);
        string game = Environment.GetEnvironmentVariable("VRISING_GAME_DIR")
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\VRising";
        refs.Add(Path.GetDirectoryName(baseline) ?? ".");
        // Job FENC8: the plugin compiles against the FRAMEWORK, and Progenitor.dll is not deployed
        // next to the plugin — it sits in BepInEx/progenitor-core, beside the reload folder the
        // plugins live in. Without both of these every source file that says `using Progenitor`
        // fails with CS0234 and no delta can ever be emitted. The reload folder itself is here for
        // the same reason at one remove: a plugin may reference a sibling plugin.
        refs.Add(outDir);
        string? bepinex = Path.GetDirectoryName(outDir.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(bepinex)) refs.Add(Path.Combine(bepinex!, "progenitor-core"));
        refs.Add(Path.Combine(game, "BepInEx", "interop"));
        refs.Add(Path.Combine(game, "BepInEx", "unity-libs"));
        refs.Add(Path.Combine(game, "BepInEx", "core"));

        return new EncWatchOptions
        {
            ProjectDir = project,
            BaselineDll = baseline,
            ReloadFolder = outDir,
            ReferenceDirs = refs,
            Once = Array.IndexOf(args, "--once") >= 0,
        };
    }

    private static string? Value(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// The folder that holds the MAIN checkout of this repository: PROGENITOR_WORKSPACE, else the
    /// parent of the main checkout found through git's metadata (a <c>.git</c> directory is the
    /// common dir; a <c>.git</c> file names a worktree gitdir whose <c>commondir</c> names it),
    /// else the parent of <paramref name="repo"/>. Same rules as tools/common/Workspace.ps1.
    /// </summary>
    private static string? FindWorkspace(string repo)
    {
        string? env = Environment.GetEnvironmentVariable("PROGENITOR_WORKSPACE");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        string? candidate = null;
        string? common = GitCommonDir(repo);
        if (common != null)
        {
            string? main = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(common));
            if (main != null) candidate = Path.GetDirectoryName(main);
        }
        candidate ??= Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo)));
        return candidate != null && Directory.Exists(candidate) ? candidate : null;
    }

    private static string? GitCommonDir(string repo)
    {
        string dotGit = Path.Combine(repo, ".git");
        if (Directory.Exists(dotGit)) return Path.GetFullPath(dotGit);
        if (!File.Exists(dotGit)) return null;
        string first = FirstLine(dotGit);
        if (!first.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        string gitDir = Path.GetFullPath(Path.Combine(repo, first.Substring("gitdir:".Length).Trim()));
        string commonFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonFile)) return gitDir;
        string common = FirstLine(commonFile);
        return common.Length == 0 ? null : Path.GetFullPath(Path.Combine(gitDir, common));
    }

    private static string FirstLine(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            return (reader.ReadLine() ?? "").Trim();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// The scenarios the delta watcher has to classify. Run it with and without the env var.
    /// </summary>
    private static void Matrix()
    {
        Console.WriteLine(EncProbe.EnvironmentVariable + "=" +
                          (Environment.GetEnvironmentVariable(EncProbe.EnvironmentVariable) ?? "(unset)") +
                          "  IsSupported=" + EncProbe.IsEnabled);

        const string v1 = EncProbe.BodyV1;
        const string v2 = EncProbe.BodyV2;
        const string withExtraMethod = "namespace Probe { public static class Target { public static string Value() { return \"v1\"; } public static int Extra() { return 7; } } }";
        const string withNewType = "namespace Probe { public static class Target { public static string Value() { return \"v2\"; } } public class Added { } }";
        const string returnTypeChanged = "namespace Probe { public static class Target { public static int Value() { return 1; } } }";

        Scenario("method body, Debug baseline", () => EncProbe.ApplyAndInvoke(EncProbe.EmitBodyDelta(v1, v2, "Value")));
        Scenario("method body, Release baseline", () => EncProbe.ApplyAndInvoke(EncProbe.EmitBodyDelta(v1, v2, "Value", optimize: true)));
        Scenario("insert a new method", () => EncProbe.ApplyAndInvoke(EncProbe.EmitBodyDelta(v1, withExtraMethod, "Extra", insert: true)));
        Scenario("method body plus a new type", () => EncProbe.ApplyAndInvoke(EncProbe.EmitBodyDelta(v1, withNewType, "Value")));
        Scenario("return type changed (rude)", () => EncProbe.ApplyAndInvoke(EncProbe.EmitBodyDelta(v1, returnTypeChanged, "Value")));
    }

    private static void Scenario(string name, Func<string> run)
    {
        string outcome;
        try { outcome = run(); }
        catch (Exception e) { outcome = "EmitDifference threw " + e.GetType().Name + ": " + e.Message; }
        Console.WriteLine("  " + name.PadRight(30) + " : " + outcome);
    }
}
