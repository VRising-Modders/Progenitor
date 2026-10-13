using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Access;

namespace Progenitor.AccessGen;

/// <summary>
/// <c>accessgen</c>: read an <c>access.txt</c> declaration file, write one C# file of typed
/// accessors.
///
/// <code>
/// dotnet run --project tools/accessgen -- \
///   --declarations src/Progenitor.Core/Access/access.txt \
///   --out src/Progenitor.Core/Access/Generated \
///   --build 248598344
///
/// dotnet run --project tools/accessgen -- --declarations ... --out ... --check
/// </code>
///
/// <c>--check</c> answers one question: did the declarations drift from the checked-in file? It
/// compares the emitted body and the declaration hash and ignores the <c>build:</c> header line,
/// so a game build bump is not reported as a stale file (R39 finding 5). The same comparison is
/// exercised from the test project, which regenerates the checked-in file and asserts it is
/// byte-identical, so CI covers the gate without running this tool.
///
/// Every field line's <c>ref</c> modifier is checked against the interop assemblies before
/// anything is written (R40 finding 3): <c>--interop &lt;dir&gt;</c>, or
/// <c>VRISING_INTEROP_DIR</c> / <c>VRISING_GAME_DIR</c>. That is metadata only — a
/// <see cref="System.Reflection.PortableExecutable.PEReader"/> over files, no <c>Assembly.Load</c>
/// and no game — but the folder is <em>required</em> when the declarations contain a field line.
/// <c>--unverified</c> generates without it, and the emitted header then says
/// <c>verified: false</c>, which <c>--check</c> compares and the generated
/// <c>Access.Describe()</c> prints — the emitted one, so a plugin's facade prints the plugin's own
/// answer rather than Core's (R41 finding 3: the two used to be byte-identical, so nothing could
/// tell them apart; R42 finding 3: only Core had a <c>Describe()</c> to say so).
///
/// The verification policy itself lives in <see cref="AccessGenerator.Verify"/>, not in
/// <see cref="Main"/>: a verdict reachable only by running this exe is a verdict nothing in
/// <c>dotnet test</c> can assert, and "what does the documented <c>--check</c> command do on a
/// machine without the game" is exactly the question CI has to answer (R42 finding 3).
///
/// Exit codes: 0 written / up to date, 1 bad arguments, unreadable declarations, a <c>ref</c>
/// modifier the interop assemblies contradict, or field lines with neither an interop folder nor
/// <c>--unverified</c>; 2 <c>--check</c> found the checked-in file out of date.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string declarationsPath = "";
        string outDirectory = "";
        string fileName = "Access.Generated.cs";
        bool check = false;
        string interopDirectory = "";
        bool interopGiven = false;
        bool unverifiedOptIn = false;
        var options = new AccessGenOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--declarations":
                case "-d":
                    if (++i >= args.Length) return Fail("--declarations needs a path");
                    declarationsPath = args[i];
                    break;
                case "--out":
                case "-o":
                    if (++i >= args.Length) return Fail("--out needs a directory");
                    outDirectory = args[i];
                    break;
                case "--file":
                    if (++i >= args.Length) return Fail("--file needs a name");
                    fileName = args[i];
                    break;
                case "--namespace":
                    if (++i >= args.Length) return Fail("--namespace needs a value");
                    options.Namespace = args[i];
                    break;
                case "--class":
                    if (++i >= args.Length) return Fail("--class needs a value");
                    options.ClassName = args[i];
                    break;
                case "--build":
                    if (++i >= args.Length) return Fail("--build needs a value");
                    options.BuildId = args[i];
                    break;
                case "--no-facade":
                    options.EmitFacade = false;
                    break;
                case "--interop":
                    if (++i >= args.Length) return Fail("--interop needs a directory");
                    interopDirectory = args[i];
                    interopGiven = true;
                    break;
                case "--unverified":
                case "--no-interop": // the old spelling; kept so existing scripts still parse.
                    unverifiedOptIn = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    return Fail("unknown argument '" + args[i] + "'");
            }
        }

        if (declarationsPath.Length == 0 || outDirectory.Length == 0)
            return Fail("--declarations and --out are both required");

        string[] lines;
        try { lines = File.ReadAllLines(declarationsPath); }
        catch (Exception e) { return Fail("cannot read " + declarationsPath + ": " + e.Message); }

        if (!AccessDeclarationParser.TryParse(lines, out var declarations, out var errors))
        {
            foreach (var error in errors) Console.Error.WriteLine(declarationsPath + ": " + error);
            return Fail(declarationsPath + ": " + errors.Count + " declaration error(s)");
        }

        if (!interopGiven) interopDirectory = DefaultInteropDirectory();
        var verification = AccessGenerator.Verify(
            declarations, interopDirectory, unverifiedOptIn, out var notices, out var verifyErrors);
        foreach (var notice in notices) Console.WriteLine("accessgen: " + notice);
        foreach (var error in verifyErrors) Console.Error.WriteLine("accessgen: " + error);
        if (verification == AccessGenerator.VerificationVerdict.Refused)
        {
            Console.Error.WriteLine("accessgen: nothing was written.");
            return 1;
        }
        options.InteropVerified = verification == AccessGenerator.VerificationVerdict.Verified;

        options.DeclarationsPath = Relative(declarationsPath);
        var target = Path.Combine(outDirectory, fileName);

        // With no --build, keep the stamp the checked-in file already carries rather than
        // replacing it with "unspecified": the generator never reads the game, so it cannot
        // invent one, and silently discarding it is how a stale file stops being visible.
        if (string.Equals(options.BuildId, DefaultBuildId, StringComparison.Ordinal) && File.Exists(target))
        {
            try
            {
                var carried = AccessGenerator.BuildIdOf(File.ReadAllText(target));
                if (carried.Length > 0) options.BuildId = carried;
            }
            catch (IOException) { /* the read below reports it */ }
            catch (UnauthorizedAccessException) { }
        }

        var text = AccessGenerator.Emit(declarations, options);

        if (check)
        {
            string existing;
            try { existing = File.ReadAllText(target).Replace("\r\n", "\n"); }
            catch (Exception e)
            {
                Console.Error.WriteLine("accessgen --check: cannot read " + target + ": " + e.Message);
                return 2;
            }
            var verdict = AccessGenerator.Check(existing, text, out var detail);
            if (verdict == AccessGenerator.CheckVerdict.Stale)
            {
                Console.Error.WriteLine("accessgen --check: " + target
                    + " is out of date; regenerate it from " + declarationsPath
                    + " (" + detail + ")");
                return 2;
            }
            // A build stamp that moved is not a declaration drift: the gate answers "did the
            // declarations change", and the stamp is a header decoration nothing resolves
            // against (R39 finding 5).
            Console.WriteLine("accessgen --check: " + target + " matches "
                + declarations.Count + " declaration(s); " + detail + ".");
            return 0;
        }

        try
        {
            Directory.CreateDirectory(outDirectory);
            // No BOM and \n line endings, so the file is byte-identical across machines.
            File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception e) { return Fail("cannot write " + target + ": " + e.Message); }

        Console.WriteLine("accessgen: wrote " + target + " (" + declarations.Count
            + " declaration(s), sha256:" + AccessDeclarationParser.Hash(declarations) + ")");
        return 0;
    }

    /// <summary>
    /// Where BepInEx puts the interop assemblies, from the same environment variables
    /// <c>Directory.Build.props</c> reads. <c>""</c> when neither is set.
    /// </summary>
    private static string DefaultInteropDirectory()
    {
        var interop = Environment.GetEnvironmentVariable("VRISING_INTEROP_DIR");
        if (!string.IsNullOrEmpty(interop)) return interop!;
        var game = Environment.GetEnvironmentVariable("VRISING_GAME_DIR");
        return string.IsNullOrEmpty(game) ? "" : Path.Combine(game!, "BepInEx", "interop");
    }

    /// <summary>
    /// Repo-relative, forward slashes, so the generated header is the same string on every
    /// machine. Falls back to the file name when the path is not under a recognizable root.
    /// </summary>
    private static string Relative(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        foreach (var root in new[] { "/src/", "/tools/", "/samples/", "/tests/" })
        {
            int at = full.LastIndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return full.Substring(at + 1);
        }
        return Path.GetFileName(full);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("accessgen: " + message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    /// <summary>The <c>--build</c> value <see cref="AccessGenOptions"/> uses when none is given.</summary>
    private const string DefaultBuildId = "unspecified";

    private const string Usage =
        "usage: accessgen --declarations <access.txt> --out <directory>\n"
        + "                 [--file <name.cs>] [--namespace <ns>] [--class <name>]\n"
        + "                 [--build <id>] [--no-facade] [--check]\n"
        + "                 [--interop <BepInEx interop dir> | --unverified]\n"
        + "\n"
        + "A declaration file with field lines needs the interop folder: --interop <dir>, or\n"
        + "VRISING_INTEROP_DIR / VRISING_GAME_DIR. --unverified generates without it and stamps\n"
        + "'verified: false' into the generated header, where --check and Access.Describe() see it.";
}
