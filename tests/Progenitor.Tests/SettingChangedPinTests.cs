using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-01: <c>RemoteInstallEvents</c> added <c>entry.SettingChanged += …</c> on an entry of Bootstrap's
/// process-lifetime config (<c>SharedConfig</c>). The multicast kept the Core generation's closure alive, so every
/// client Core swap was refused from startup (pin-diag holder <c>ProcessHost.SharedConfig[].value.SettingChanged</c>).
/// Core subscribes through <c>ConfigHelper.AddSettingChanged</c>, which unsubscribes on unload. A direct subscription
/// is allowed only where the file is Core's own (opened by that generation) or the code unsubscribes itself.
/// </summary>
public class SettingChangedPinTests
{
    /// <summary>Files reviewed to subscribe safely: their own Core-owned ConfigFile, or a matching unsubscribe.</summary>
    private static readonly HashSet<string> Reviewed = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Config", "ConfigHelper.cs"),     // the tracked helper itself
        Path.Combine("Networking", "ConfigSync.cs"),   // unsubscribes SettingChangedHandler on uninstall
        Path.Combine("Ui", "UiKeybindHost.cs"),        // binds on progenitor.ui.cfg, a ConfigFile this generation opens
    };

    [Fact]
    public void Core_does_not_subscribe_SettingChanged_directly_outside_reviewed_files()
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
                if (trimmed.Contains("SettingChanged +=", StringComparison.Ordinal))
                    offenders.Add(relative + ":" + (i + 1) + ": " + trimmed);
            }
        }

        Assert.True(offenders.Count == 0,
            "Subscribe through ConfigHelper.AddSettingChanged (a direct += on Bootstrap's config pins the Core generation):\n"
            + string.Join("\n", offenders));
    }
}
