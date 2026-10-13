using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>ProjectM.UIAssetSubSceneLoader_DefaultWorld</c> derives from
/// <c>AssetSubSceneStreamingHandler&lt;UIAssetSubSceneLoader_DefaultWorld.Singleton&gt;</c>, and the
/// nested <c>Singleton</c> holds a field of <c>AssetSubSceneStreamingHandler&lt;Singleton&gt;.InternalSingleton</c>.
/// The game's .NET 6 runtime refuses to load that struct (reproduced offline on 6.0.35; .NET 8 loads it),
/// so any method that names the loader fails to JIT with a <c>TypeLoadException</c>. Every Core swap
/// since 2026-09-13 logged <c>Ui.Uninstall: Could not load type '…+Singleton'</c> and skipped the rest of
/// <c>Ui.Uninstall</c>. Core must not reference the type.
/// </summary>
public class UnloadableInteropTypeTests
{
    private static readonly string[] Unloadable =
    {
        "UIAssetSubSceneLoader_DefaultWorld",
    };

    [Fact]
    public void Source_does_not_reference_interop_types_the_game_runtime_cannot_load()
    {
        string root = Path.Combine(SourcePin.RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = path.Substring(root.Length + 1);
            string sep = Path.DirectorySeparatorChar.ToString();
            if (relative.Contains(sep + "bin" + sep, StringComparison.Ordinal)
                || relative.Contains(sep + "obj" + sep, StringComparison.Ordinal))
                continue;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;
                foreach (string name in Unloadable)
                {
                    if (trimmed.Contains(name, StringComparison.Ordinal))
                        offenders.Add(relative + ":" + (i + 1) + ": " + trimmed);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These interop types fail to load on the game's .NET 6 runtime; a method naming one cannot JIT:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Ui_uninstall_guards_the_native_entry_teardown()
    {
        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        string body = SourcePin.MethodBody(ui, "internal static void Uninstall()");
        SourcePin.AssertStatementExactly(body, "try { NativeModsEntry.Uninstall(); }");

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string watch = SourcePin.MethodBody(entry, "internal static void UninstallLifecycleWatch()");
        SourcePin.AssertStatementExactly(watch, "try { ModsWorkspaceChrome.ReleasePlayBackdropCache(); }");
    }
}
