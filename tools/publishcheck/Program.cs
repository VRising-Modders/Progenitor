using System;
using System.IO;
using System.Text.Json;

namespace Progenitor.PublishCheck;

/// <summary>
/// <c>publishcheck</c> — the pre-publish checker of docs/publishing.md. Usually run through
/// <c>tools/publish-check.ps1</c>.
///
/// <code>
/// publishcheck check &lt;package.zip | package folder&gt; [--json]
/// publishcheck progenitor-json &lt;plugin.dll&gt; [--hot-loadable] [--id &lt;guid&gt;] [--out &lt;progenitor.json&gt;]
/// </code>
///
/// Exit codes: 0 every check passed or warned, 1 a check failed, 2 usage or input error.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length < 2) return Usage();
        switch (args[0])
        {
            case "check": return Check(args);
            case "progenitor-json": return WriteJson(args);
            default: return Usage();
        }
    }

    private static int Check(string[] args)
    {
        bool json = Array.IndexOf(args, "--json") >= 0;
        var report = PublishChecker.CheckPath(args[1]);
        if (json)
        {
            var rows = new object[report.Results.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var r = report.Results[i];
                rows[i] = new { id = r.Id, status = r.Status.ToString().ToLowerInvariant(), message = r.Message };
            }

            Console.WriteLine(JsonSerializer.Serialize(new { package = report.Package, failed = report.Failed, results = rows },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("Publish check: " + report.Package);
            foreach (var r in report.Results)
                Console.WriteLine(Word(r.Status) + "  " + r.Id.PadRight(26) + " " + r.Message);
            Console.WriteLine();
            Console.WriteLine(report.Count(CheckStatus.Pass) + " passed, " + report.Count(CheckStatus.Warn) + " warnings, "
                + report.Count(CheckStatus.Fail) + " failed.");
        }

        return report.Failed ? 1 : 0;
    }

    private static int WriteJson(string[] args)
    {
        string dll = args[1];
        bool hot = false;
        string? id = null, outPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hot-loadable": hot = true; break;
                case "--id": id = i + 1 < args.Length ? args[++i] : null; break;
                case "--out": outPath = i + 1 < args.Length ? args[++i] : null; break;
                default: return Usage();
            }
        }

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine("publishcheck: no file at " + dll);
            return 2;
        }

        string? text = ProgenitorJsonWriter.Build(File.ReadAllBytes(dll), hot, id, out var error);
        if (text == null)
        {
            Console.Error.WriteLine("publishcheck: cannot write progenitor.json: " + error);
            return 2;
        }

        if (outPath == null) Console.Write(text);
        else
        {
            ProgenitorJsonWriter.Write(outPath, text);
            Console.WriteLine("Wrote " + Path.GetFullPath(outPath));
        }

        return 0;
    }

    private static string Word(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        _ => "FAIL",
    };

    private static int Usage()
    {
        Console.Error.WriteLine("usage: publishcheck check <package.zip|folder> [--json]");
        Console.Error.WriteLine("       publishcheck progenitor-json <plugin.dll> [--hot-loadable] [--id <guid>] [--out <progenitor.json>]");
        return 2;
    }
}
