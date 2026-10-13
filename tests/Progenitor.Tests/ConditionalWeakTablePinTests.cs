using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-05 (client process dump): <c>ClientModReportClient</c> cached rows in a
/// <c>ConditionalWeakTable&lt;Assembly, List&lt;ClientModObservation&gt;&gt;</c>. One key was a BepInEx patcher assembly,
/// which lives as long as the process. A table entry is a dependent handle: the value stays alive while the key
/// does, and a value whose type is Core's (here the <c>List&lt;ClientModObservation&gt;</c> instantiation) keeps the
/// Core generation's LoaderAllocator alive with it. Every client Core swap after a join was refused. SOS
/// <c>gcroot</c> does not follow dependent handles, so it reported no root. A table is allowed only where every key is
/// an object of the same Core generation, so key and value die together.
/// </summary>
public class ConditionalWeakTablePinTests
{
    /// <summary>Files reviewed to key their table only on objects this Core generation creates.</summary>
    private static readonly HashSet<string> Reviewed = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Ui", "InstallModsPane.cs"),   // IInstallCatalog: Core's InstallCatalogService
        Path.Combine("Ui", "UiNav.cs"),             // UiHandle: Core's own handles (NAV1 row hooks and skips)
        Path.Combine("Units", "UnitAuras.cs"),      // IUnitHandle: Core's unit handles
        Path.Combine("Units", "UnitBody.cs"),       // IUnitHandle
        Path.Combine("Units", "UnitStatsBody.cs"),  // IUnitHandle
    };

    [Fact]
    public void Core_declares_ConditionalWeakTable_only_in_reviewed_files()
    {
        string root = Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core");
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = path.Substring(root.Length + 1);
            string rooted = Path.DirectorySeparatorChar + relative;
            if (rooted.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rooted.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (Reviewed.Contains(relative)) continue;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.Contains("ConditionalWeakTable<", StringComparison.Ordinal) || trimmed.Contains("DependentHandle", StringComparison.Ordinal))
                    offenders.Add(relative + ":" + (i + 1) + ": " + trimmed);
            }
        }

        Assert.True(offenders.Count == 0,
            "A ConditionalWeakTable keyed on an object that outlives this Core generation (an Assembly, a Bootstrap or "
            + "game object) keeps its Core-typed values, and the generation, alive. Key by a string or id in a "
            + "Core-owned Dictionary instead, or review the keys and add the file here:\n"
            + string.Join("\n", offenders));
    }
}
