using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Compat;

namespace Progenitor.DriftMapTool;

/// <summary>
/// <c>driftmap</c> — diffs two interop assembly sets and writes <c>driftmap-{build}.json</c>.
///
/// <code>
/// dotnet run --project tools/driftmap -- \
///     --old  &lt;archived BepInEx/interop from the previous build&gt; \
///     --new  &lt;the current BepInEx/interop&gt; \
///     --side client --build 248100992-6a147771-ec12531-66fe4b61f7a0d186 \
///     --out  src/Progenitor.Core/... or BepInEx/progenitor-core/driftmap-&lt;build&gt;.json
/// </code>
///
/// <para><b>Where <c>--old</c> comes from.</b> BepInEx regenerates <c>BepInEx/interop</c> in
/// place on a game update, so the previous build's set exists only if something kept it.
/// Something does: Progenitor's resilience pass archives each new build's interop folder as
/// <c>interop-{side}-{build}/</c> beside the address tables in
/// <c>BepInEx/progenitor-core/addresslib/</c> (<c>Progenitor.Resilience.InteropArchive</c>, job
/// FUPD4), keeping the newest three per side. <c>pwsh tools/resilience-report.ps1</c> lists them
/// and prints the exact command line for the build installed now. Nothing on the machine can
/// produce an <c>--old</c> folder after the fact.
///
/// It writes only the <c>generated</c> block. A <c>hand</c> block in the output file (or in a
/// file named by <c>--hand</c>) is carried over verbatim, because a hand rule is a
/// source-controlled decision a regeneration must never silently drop (research
/// compat-rewriter.md §2.2, §4.5). Reads with System.Reflection.Metadata; never loads an
/// assembly and never touches a game process.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string? oldDir = null, newDir = null, side = null, build = null, outPath = null, handPath = null;
        var prefixes = new List<string>(DriftMap.GameAssemblyPrefixes);
        bool customPrefixes = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--old": oldDir = next; i++; break;
                case "--new": newDir = next; i++; break;
                case "--side": side = next; i++; break;
                case "--build": build = next; i++; break;
                case "--out": outPath = next; i++; break;
                case "--hand": handPath = next; i++; break;
                case "--prefixes":
                    if (next != null)
                    {
                        if (!customPrefixes) { prefixes.Clear(); customPrefixes = true; }
                        prefixes.AddRange(next.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    }
                    i++;
                    break;
                case "-h":
                case "--help":
                    Usage();
                    return 0;
                default:
                    Console.Error.WriteLine("unknown argument: " + a);
                    Usage();
                    return 2;
            }
        }

        if (oldDir == null || newDir == null || side == null || build == null)
        {
            Console.Error.WriteLine("--old, --new, --side and --build are all required.");
            Console.Error.WriteLine(
                "--old is an archived interop set (interop-<side>-<build> beside the address tables); "
                + "run `pwsh tools/resilience-report.ps1` to list the ones on disk.");
            Usage();
            return 2;
        }
        if (!Directory.Exists(oldDir)) { Console.Error.WriteLine("no such directory: " + oldDir); return 2; }
        if (!Directory.Exists(newDir)) { Console.Error.WriteLine("no such directory: " + newDir); return 2; }

        var notes = new List<string>();
        var before = AssemblySurface.Read(oldDir, prefixes, notes);
        var after = AssemblySurface.Read(newDir, prefixes, notes);
        var diff = DriftDiff.Compare(before, after);

        var rules = new List<DriftRule>(diff.Rules);
        int handCount = 0;
        string? handJson = ReadHandBlockSource(handPath, outPath);
        if (handJson != null)
        {
            var existing = DriftMap.Parse(handJson, out var error, out var refusals);
            if (existing == null)
            {
                Console.Error.WriteLine("refusing to overwrite: the existing map is malformed (" + error
                                        + "). Fix it or delete it; hand rules must not be lost silently.");
                return 3;
            }
            foreach (var rule in existing.Rules)
                if (rule.Source == DriftSource.Hand) { rules.Add(rule); handCount++; }
            foreach (string refusal in refusals) notes.Add("existing map: " + refusal);
        }

        var map = DriftMap.Create(side, build, rules);
        string json = map.ToJson();

        foreach (string note in diff.Notes) Console.Error.WriteLine("note: " + note);
        foreach (string note in notes) Console.Error.WriteLine("note: " + note);
        Console.Error.WriteLine("driftmap " + side + "-" + build + ": "
                                + map.GeneratedCount + " generated, " + map.HandCount + " hand ("
                                + handCount + " carried over), " + diff.Notes.Count + " unfixable");

        if (outPath == null) Console.Out.Write(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            File.WriteAllText(outPath, json);
            Console.Error.WriteLine("wrote " + outPath);
        }
        return 0;
    }

    private static string? ReadHandBlockSource(string? handPath, string? outPath)
    {
        try
        {
            if (handPath != null && File.Exists(handPath)) return File.ReadAllText(handPath);
            if (handPath == null && outPath != null && File.Exists(outPath)) return File.ReadAllText(outPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("could not read the existing hand block: " + e.Message);
        }
        return null;
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            "driftmap --old <dir> --new <dir> --side <client|server> --build <id>\n" +
            "         [--out <driftmap-<build>.json>] [--hand <file>] [--prefixes ProjectM,Unity,Stunlock]\n" +
            "\n" +
            "Diffs two interop assembly sets and writes the generated half of a drift map.\n" +
            "--old is an archived interop set: BepInEx/progenitor-core/addresslib/interop-<side>-<build>,\n" +
            "written by Progenitor's resilience pass. `pwsh tools/resilience-report.ps1` lists the\n" +
            "archives on disk and prints the command line for the build installed now.\n" +
            "A hand block in --out (or --hand) is preserved verbatim. Layout changes -\n" +
            "a moved [FieldOffset], a changed struct size - are never emitted: interop\n" +
            "regeneration already absorbs them and a plugin's IL holds no number for them.");
    }
}
