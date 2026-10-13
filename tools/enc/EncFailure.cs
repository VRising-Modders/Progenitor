// Job FENC8 (research evidence 159, second half): what the watcher's exit code means.
//
// docs/dev-loop.md documents three codes for `--watch`, and an unattended script reads them:
//
//   0  clean pass — a delta was written (or there was nothing to do)
//   2  RUDE edit  — the running process cannot take this change; rebuild and redeploy
//   3  SETUP      — the watcher could not do its job at all: no project, no baseline image,
//                   no portable PDB, or a compilation that does not build
//
// Evidence 159 caught the gap between 2 and 3: the compilation failed with CS0009 (a native file
// in the reference set) and the failure was reported through the same arm as a rude edit. A rude
// edit is a statement about the developer's edit and the answer is a redeploy; a compilation that
// does not build is a statement about the watcher's inputs and a redeploy fixes nothing. So a
// compile failure is 3, an emit failure stays 2, and neither is ever 0.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Progenitor.Enc;

/// <summary>Which half of the pipeline failed. The exit code follows from this and nothing else.</summary>
public enum EncFailureKind
{
    /// <summary>Roslyn refused to build the compilation (bad source, or a bad reference set).</summary>
    Compile,

    /// <summary>The compilation built and EmitDifference could not turn the edit into a delta.</summary>
    Emit,
}

/// <summary>
/// The compilation itself did not build. Distinct from every other exception the emit path can
/// throw, because it is the one that means "setup", not "rude edit".
/// </summary>
public sealed class EncCompileException : Exception
{
    public EncCompileException(string message) : base(message) { }
}

public static class EncExitCodes
{
    public const int Ok = 0;
    public const int Rude = 2;
    public const int Error = 3;

    /// <summary>A failed compile is <see cref="Error"/>; a failed emit is the rude-edit signal.</summary>
    public static int For(EncFailureKind kind) => kind == EncFailureKind.Compile ? Error : Rude;

    /// <summary>The same mapping for what the emit path actually throws.</summary>
    public static int For(Exception failure) =>
        For(failure is EncCompileException ? EncFailureKind.Compile : EncFailureKind.Emit);

    /// <summary>
    /// The first error in <paramref name="diagnostics"/>, as "CS0009 Metadata file '...' could not
    /// be opened ...", or null when there is none. One diagnostic, not the whole list: Roslyn
    /// reports the same bad reference once per file that uses it, and the first one names the cause.
    /// </summary>
    public static string? FirstError(IEnumerable<Diagnostic> diagnostics)
    {
        var first = diagnostics?.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (first == null) return null;
        int more = diagnostics!.Count(d => d.Severity == DiagnosticSeverity.Error) - 1;
        return first.Id + " " + first.GetMessage() + (more > 0 ? " (+" + more + " more error(s))" : "");
    }
}
