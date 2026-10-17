// Job FENC8 (research evidence 159): the metadata reference set the Edit-and-Continue watcher
// hands Roslyn.
//
// The watcher sweeps whole folders for *.dll — the plugin's own output, BepInEx/interop,
// BepInEx/unity-libs and BepInEx/core — because that is the only way to reproduce the plugin's
// compile-time reference set without an MSBuild evaluation. Those folders are not all managed:
// BepInEx/core carries `dobby.dll`, a NATIVE image, and `MetadataReference.CreateFromFile` is
// lazy, so nothing complains until the compilation is used and then Roslyn fails the WHOLE
// compilation with
//
//   CS0009 Metadata file '...\BepInEx\core\dobby.dll' could not be opened
//          -- PE image doesn't contain managed metadata
//
// which is what evidence 159 saw live: a correct baseline, a correct sequence, and not one delta.
// So every candidate is probed with PEReader/MetadataReader before it joins the set, and anything
// that is not a readable managed PE (a native image, a resource-only DLL, a file locked by another
// process, a truncated download) is skipped and counted. The count is logged ONCE — this is a
// property of the install, not of the edit, and it must not repeat per poll.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Progenitor.Enc;

/// <summary>What <see cref="EncReferences.Load"/> found: the usable references and what it skipped.</summary>
public sealed class EncReferenceSet
{
    /// <summary>Managed assemblies, deduplicated by file stem, first folder wins.</summary>
    public List<MetadataReference> References = new();

    /// <summary>File names (not paths) of the candidates that were not usable managed assemblies.</summary>
    public List<string> Skipped = new();
}

public static class EncReferences
{
    /// <summary>
    /// True when <paramref name="path"/> is a PE image carrying readable ECMA-335 metadata — the
    /// only thing Roslyn can take as a reference. Native images (dobby.dll and friends) have no
    /// CLI header, so <see cref="PEReader.HasMetadata"/> is false; a file that is not a PE at all,
    /// or one that cannot be opened, throws and is equally unusable. Both answer false: the caller
    /// treats "not managed" and "cannot be read" the same way, because the outcome for the
    /// compilation is identical.
    /// </summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            // HasMetadata only says a CLI header points somewhere. Read the table stream too, so a
            // truncated or malformed image is rejected here rather than inside the compilation.
            MetadataReader reader = pe.GetMetadataReader();
            return reader.MetadataLength > 0;
        }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Collect every managed assembly under <paramref name="dirs"/>, then the running runtime's own
    /// core assemblies from TRUSTED_PLATFORM_ASSEMBLIES so no reference-assembly package is needed.
    /// Deduplicated by file stem: the first folder that supplies a name wins, which is why a skipped
    /// native file releases its name — a managed assembly with the same stem in a later folder is
    /// still allowed in.
    /// </summary>
    public static EncReferenceSet Load(IEnumerable<string> dirs)
    {
        var set = new EncReferenceSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Skip(string path, HashSet<string> names, string stem)
        {
            names.Remove(stem);
            if (skippedNames.Add(Path.GetFileName(path))) set.Skipped.Add(Path.GetFileName(path));
        }

        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // Core ships as Progenitor.core in the single-folder install (never a .dll under plugins/);
            // its stem is still Progenitor.
            var files = new List<string>(Directory.GetFiles(dir, "*.dll"));
            files.AddRange(Directory.GetFiles(dir, "Progenitor.core"));
            foreach (string dll in files)
            {
                string stem = Path.GetFileNameWithoutExtension(dll);
                if (!seen.Add(stem)) continue;
                if (!IsManagedAssembly(dll)) { Skip(dll, seen, stem); continue; }
                try { set.References.Add(MetadataReference.CreateFromFile(dll)); }
                catch (Exception) { Skip(dll, seen, stem); }
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!stem.StartsWith("System.", StringComparison.Ordinal)
                    && stem != "netstandard" && stem != "mscorlib") continue;
                if (!seen.Add(stem) || !File.Exists(path)) continue;
                // The TPA list is the runtime's own, so anything on it is managed by construction;
                // it is still probed, because a broken install should not fail the compilation with
                // a diagnostic that points at a file the developer never chose.
                if (!IsManagedAssembly(path)) { Skip(path, seen, stem); continue; }
                try { set.References.Add(MetadataReference.CreateFromFile(path)); }
                catch (Exception) { Skip(path, seen, stem); }
            }
        }
        return set;
    }

    /// <summary>
    /// The one line the watcher logs about skipped candidates, or null when there were none.
    /// A few names are enough to recognize the cause (dobby.dll is the usual one); the count is
    /// what says whether something unexpected is being dropped.
    /// </summary>
    public static string? SkippedMessage(IReadOnlyList<string> skipped, int maxNames = 3)
    {
        if (skipped == null || skipped.Count == 0) return null;
        var names = new List<string>();
        for (int i = 0; i < skipped.Count && i < maxNames; i++) names.Add(skipped[i]);
        string tail = skipped.Count > names.Count ? ", ..." : "";
        return "skipped " + skipped.Count + " non-managed file(s) in the reference set ("
               + string.Join(", ", names) + tail + ")";
    }
}
