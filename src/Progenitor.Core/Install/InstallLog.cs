using System;

namespace Progenitor.Install;

/// <summary>
/// INSTALLFAIL1: the one place the local install pipeline writes to the log. Before it, nothing
/// between the confirmation's Yes and the file on disk logged at all, so a write the row did not
/// reflect (and every refusal after the dialog closed) left no line behind. Every outcome of a
/// confirmed install, a download and a set plan goes through here with its reason.
/// </summary>
internal static class InstallLog
{
    /// <summary>Tests read the lines here; null in the game.</summary>
    internal static Action<string, bool>? TestSink { get; set; }

    public static void Info(string line) => Write(line, warn: false);

    public static void Warn(string line) => Write(line, warn: true);

    private static void Write(string line, bool warn)
    {
        try { TestSink?.Invoke(line, warn); }
        catch { /* a test sink never breaks an install */ }

        try
        {
            if (warn) Plugin.Logger?.LogWarning(line);
            else Plugin.Logger?.LogInfo(line);
        }
        catch { /* logging is evidence, never a gate */ }
    }
}
