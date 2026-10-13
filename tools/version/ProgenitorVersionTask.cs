// Computes a Progenitor component's build version from git. Compiled by MSBuild's
// RoslynCodeTaskFactory from tools/version/ProgenitorVersion.targets; it is not part of any
// project. The rules are documented in docs/versioning.md; keep the two in step.
//
//   <year>.<month>.<release>          the component is unchanged since main
//   <year>.<month>.<release>.<sub>    the component has unmerged commits or uncommitted changes
//
// Main line: the first-parent history of base = merge-base(HEAD, first resolvable main ref).
// Release of a main commit: the last merge commit (2+ parents) at or before it on that line;
//   year.month = that merge's UTC committer month; release = merges on the line, up to and
//   including it, in that month. A main commit with no merge at or before it is <its month>.0.
// A component's version is the release of the newest main-line commit whose first-parent diff
//   touches the component's inputs (its project directory, files it links from outside that
//   directory, referenced projects' inputs, and the shared build files). sub = commits in
//   base..HEAD touching those inputs, plus 1 when an input has uncommitted changes.
// With no project the scope is the whole repository.
//
// Git runs once per build per MSBuild node (log of main, log of base..HEAD, status); every
// project reuses the parsed history. Failure never fails the build: 0.0.0.0 and a warning.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class ProgenitorComputeVersionTask : Task
{
    [Required]
    public string RepoRoot { get; set; } = "";

    /// <summary>git executable. Empty means "git" on PATH.</summary>
    public string GitExe { get; set; } = "";

    /// <summary>Semicolon-separated refs tried in order for the main branch.</summary>
    public string MainRefs { get; set; } = "origin/main;main";

    /// <summary>Project file whose inputs scope the version. Empty: the whole repository.</summary>
    public string ProjectFile { get; set; } = "";

    /// <summary>Extra input paths (files or directories), absolute or repo-relative.</summary>
    public string ExtraInputs { get; set; } = "";

    /// <summary>Repo-relative files every project's version depends on.</summary>
    public string SharedInputs { get; set; } = "Directory.Build.props;Directory.Build.targets";

    [Output] public string Version { get; set; } = "";
    [Output] public string Revision { get; set; } = "";
    [Output] public string Inputs { get; set; } = "";

    // Cached history layout (BCL types only: the task assembly may be compiled more than once
    // per node, so a type of our own would not survive the cache).
    private const int HError = 0, HBase = 1, HMainShas = 2, HMainVersions = 3, HMainFiles = 4,
        HBranchShas = 5, HBranchFiles = 6, HDirty = 7, HLength = 8;

    public override bool Execute()
    {
        string git = string.IsNullOrWhiteSpace(GitExe) ? "git" : GitExe.Trim();
        string root = Path.GetFullPath(RepoRoot).TrimEnd('\\', '/');
        string key = "Progenitor.Version|" + root + "|" + git + "|" + MainRefs;

        var engine = BuildEngine as IBuildEngine4;
        object[] history = engine != null ? engine.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) as object[] : null;
        bool fresh = history == null;
        if (history == null)
        {
            history = ReadHistory(git, root);
            if (engine != null) engine.RegisterTaskObject(key, history, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: false);
        }

        var error = history[HError] as string;
        if (error != null)
        {
            if (fresh) Log.LogWarning("Progenitor version: " + error + " Using 0.0.0.0. See docs/versioning.md.");
            else Log.LogMessage(MessageImportance.Low, "Progenitor version: " + error + " Using 0.0.0.0.");
            Version = "0.0.0.0";
            Revision = "";
            return true;
        }

        var inputs = CollectInputs(root);
        Inputs = inputs == null ? "(repository)" : string.Join(";", inputs);

        var mainShas = (string[])history[HMainShas];
        var mainVersions = (string[])history[HMainVersions];
        var mainFiles = (string[][])history[HMainFiles];
        var branchShas = (string[])history[HBranchShas];
        var branchFiles = (string[][])history[HBranchFiles];
        var dirty = (string[])history[HDirty];

        // Newest main-line commit that touched an input; the base's release when none did
        // (a component that does not exist on main yet).
        string version = mainVersions[0];
        string revision = mainShas[0];
        for (int i = 0; i < mainShas.Length; i++)
        {
            if (!Touches(mainFiles[i], inputs)) continue;
            version = mainVersions[i];
            revision = mainShas[i];
            break;
        }

        int sub = 0;
        bool branchRevision = false;
        for (int i = 0; i < branchShas.Length; i++)
        {
            if (!Touches(branchFiles[i], inputs)) continue;
            sub++;
            if (!branchRevision) { revision = branchShas[i]; branchRevision = true; }
        }
        bool isDirty = Touches(dirty, inputs);
        if (isDirty) sub++;

        Version = sub > 0 ? version + "." + sub.ToString(CultureInfo.InvariantCulture) : version;
        Revision = (revision.Length > 10 ? revision.Substring(0, 10) : revision) + (isDirty ? ".dirty" : "");
        Log.LogMessage(MessageImportance.Low, "Progenitor version " + Version + "+" + Revision + " from inputs " + Inputs);
        return true;
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>Repo-relative, '/'-separated input paths; a trailing '/' marks a directory.
    /// Null means the whole repository.</summary>
    private List<string> CollectInputs(string root)
    {
        if (string.IsNullOrWhiteSpace(ProjectFile) && string.IsNullOrWhiteSpace(ExtraInputs)) return null;
        var set = new List<string>();
        foreach (string s in SharedInputs.Split(';'))
            AddInput(set, root, Path.Combine(root, s.Trim()), directory: false);
        foreach (string s in ExtraInputs.Split(';'))
        {
            string t = s.Trim();
            if (t.Length == 0) continue;
            string full = Path.GetFullPath(Path.IsPathRooted(t) ? t : Path.Combine(root, t));
            AddInput(set, root, full, directory: Directory.Exists(full) || t.EndsWith("/") || t.EndsWith("\\"));
        }
        if (!string.IsNullOrWhiteSpace(ProjectFile))
            AddProject(set, root, Path.GetFullPath(ProjectFile), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return set;
    }

    private static readonly Regex ProjectReferenceRx = new Regex("<ProjectReference\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
    private static readonly Regex LinkedItemRx = new Regex("<(?:Compile|EmbeddedResource|Content|None|AdditionalFiles)\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);

    /// <summary>The project's directory, every item it includes from outside that directory, and
    /// the same for each referenced project (transitively). Paths with MSBuild properties are
    /// skipped; a wildcard contributes the directory before its first wildcard.</summary>
    private static void AddProject(List<string> set, string root, string projectFile, HashSet<string> visited)
    {
        if (!visited.Add(projectFile)) return;
        string dir = Path.GetDirectoryName(projectFile) ?? root;
        AddInput(set, root, dir, directory: true);
        string text;
        try { text = File.ReadAllText(projectFile); }
        catch { return; }

        foreach (Match m in LinkedItemRx.Matches(text))
        {
            foreach (string part in m.Groups[1].Value.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0 || p.Contains("$(") || p.Contains("@(")) continue;
                int wild = p.IndexOfAny(new[] { '*', '?' });
                bool isDir = false;
                if (wild >= 0)
                {
                    int sep = p.LastIndexOfAny(new[] { '\\', '/' }, wild);
                    if (sep < 0) continue; // a wildcard inside the project directory itself
                    p = p.Substring(0, sep + 1);
                    isDir = true;
                }
                string full = Path.GetFullPath(Path.Combine(dir, p));
                if (IsUnder(full, dir)) continue;
                AddInput(set, root, full, isDir);
            }
        }
        foreach (Match m in ProjectReferenceRx.Matches(text))
        {
            string p = m.Groups[1].Value.Trim();
            if (p.Length == 0 || p.Contains("$(")) continue;
            AddProject(set, root, Path.GetFullPath(Path.Combine(dir, p)), visited);
        }
    }

    private static bool IsUnder(string full, string dir)
    {
        string d = dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddInput(List<string> set, string root, string full, bool directory)
    {
        if (!IsUnder(full, root)) return; // outside the repository: git cannot see it
        string rel = full.Substring(root.Length + 1).Replace('\\', '/').TrimEnd('/');
        if (rel.Length == 0) return;
        if (directory) rel += "/";
        foreach (string e in set) if (string.Equals(e, rel, StringComparison.OrdinalIgnoreCase)) return;
        set.Add(rel);
    }

    private static bool Touches(string[] files, List<string> inputs)
    {
        if (files.Length == 0) return false;
        if (inputs == null) return true;
        foreach (string f in files)
        {
            foreach (string i in inputs)
            {
                if (i.EndsWith("/"))
                {
                    if (f.StartsWith(i, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(f, i, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ git

    private object[] ReadHistory(string git, string root)
    {
        var h = new object[HLength];
        h[HError] = ReadHistoryInto(h, git, root);
        return h;
    }

    private string ReadHistoryInto(object[] h, string git, string root)
    {
        string baseSha = null;
        string tried = null;
        foreach (string raw in MainRefs.Split(';'))
        {
            string r = raw.Trim();
            if (r.Length == 0) continue;
            tried = tried == null ? r : tried + ", " + r;
            if (Run(git, root, "merge-base HEAD " + r, out string outText, out _) == 0 && outText.Trim().Length > 0)
            {
                baseSha = outText.Trim();
                break;
            }
        }
        if (baseSha == null)
        {
            if (Run(git, root, "rev-parse HEAD", out _, out string err) != 0)
                return "git is unavailable or '" + root + "' is not a git work tree (" + FirstLine(err) + ").";
            return "no merge base between HEAD and any of [" + tried + "] (fetch origin/main; shallow clones need full history).";
        }
        h[HBase] = baseSha;

        const string Format = "--format=%x1e%H%x1f%P%x1f%ct";
        if (Run(git, root, "-c core.quotePath=false log --first-parent --diff-merges=first-parent --name-only " + Format + " " + baseSha,
                out string mainLog, out string e1) != 0)
            return "cannot read main's first-parent history (" + FirstLine(e1) + ").";
        var main = ParseLog(mainLog);
        if (main.Count == 0) return "main's history is empty.";

        // Oldest to newest: each merge's release is the count of merges so far in its UTC month;
        // a direct commit takes the release of the last merge before it.
        var perMonth = new Dictionary<int, int>();
        var versions = new string[main.Count];
        string lastRelease = null;
        for (int i = main.Count - 1; i >= 0; i--)
        {
            var c = main[i];
            DateTime utc = DateTimeOffset.FromUnixTimeSeconds(c.Seconds).UtcDateTime;
            if (c.Parents >= 2)
            {
                int ym = utc.Year * 100 + utc.Month;
                perMonth.TryGetValue(ym, out int n);
                perMonth[ym] = ++n;
                lastRelease = utc.Year.ToString(CultureInfo.InvariantCulture) + "." + utc.Month.ToString(CultureInfo.InvariantCulture)
                    + "." + n.ToString(CultureInfo.InvariantCulture);
            }
            versions[i] = lastRelease
                ?? utc.Year.ToString(CultureInfo.InvariantCulture) + "." + utc.Month.ToString(CultureInfo.InvariantCulture) + ".0";
        }
        h[HMainShas] = main.ConvertAll(c => c.Sha).ToArray();
        h[HMainVersions] = versions;
        h[HMainFiles] = main.ConvertAll(c => c.Files).ToArray();

        var branch = new List<Commit>();
        if (Run(git, root, "rev-parse HEAD", out string head, out _) == 0 && head.Trim() != baseSha)
        {
            if (Run(git, root, "-c core.quotePath=false log --diff-merges=first-parent --name-only " + Format + " " + baseSha + "..HEAD",
                    out string branchLog, out string e2) != 0)
                return "cannot list commits since the merge base (" + FirstLine(e2) + ").";
            branch = ParseLog(branchLog);
        }
        h[HBranchShas] = branch.ConvertAll(c => c.Sha).ToArray();
        h[HBranchFiles] = branch.ConvertAll(c => c.Files).ToArray();

        if (Run(git, root, "-c core.quotePath=false status --porcelain=v1 -z --untracked-files=all", out string status, out string e3) != 0)
            return "git status failed (" + FirstLine(e3) + ").";
        var dirty = new List<string>();
        string[] tokens = status.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t.Length < 4) continue;
            char x = t[0];
            dirty.Add(t.Substring(3));
            if ((x == 'R' || x == 'C') && i + 1 < tokens.Length) dirty.Add(tokens[++i]);
        }
        h[HDirty] = dirty.ToArray();
        return null;
    }

    private sealed class Commit
    {
        public string Sha = "";
        public int Parents;
        public long Seconds;
        public string[] Files = new string[0];
    }

    private static List<Commit> ParseLog(string log)
    {
        var list = new List<Commit>();
        foreach (string record in log.Split('\x1e'))
        {
            if (record.Trim().Length == 0) continue;
            string[] lines = record.Replace("\r", "").Split('\n');
            string[] head = lines[0].Split('\x1f');
            if (head.Length < 3) continue;
            var c = new Commit { Sha = head[0].Trim() };
            string parents = head[1].Trim();
            c.Parents = parents.Length == 0 ? 0 : parents.Split(' ').Length;
            long.TryParse(head[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Seconds);
            var files = new List<string>();
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].Length > 0) files.Add(lines[i]);
            c.Files = files.ToArray();
            list.Add(c);
        }
        return list;
    }

    private static int Run(string git, string root, string args, out string stdout, out string stderr)
    {
        stdout = "";
        stderr = "";
        try
        {
            var psi = new ProcessStartInfo(git, "-C \"" + root + "\" " + args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Never take .git/index.lock: other builds and agents share this tree.
            psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var p = Process.Start(psi))
            {
                if (p == null) { stderr = "could not start " + git; return -1; }
                var errTask = p.StandardError.ReadToEndAsync();
                stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    stderr = "git timed out";
                    return -1;
                }
                stderr = errTask.Result;
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return -1;
        }
    }

    private static string FirstLine(string s)
    {
        s = (s ?? "").Trim();
        int nl = s.IndexOf('\n');
        return nl >= 0 ? s.Substring(0, nl).Trim() : s;
    }
}
