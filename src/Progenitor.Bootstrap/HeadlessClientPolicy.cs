using System;

namespace Progenitor;

/// <summary>
/// Game-free half of <see cref="HeadlessClient"/>: when the batch-mode shim may install.
/// </summary>
internal static class HeadlessClientPolicy
{
    /// <summary>Only a tagged extra client that asked for it and really runs with -batchmode.</summary>
    public static bool ShouldInstall(bool isServerProcess, string? instanceTag, string? headlessFlag, bool hasBatchModeArg)
    {
        if (isServerProcess) return false;
        if (string.IsNullOrWhiteSpace(instanceTag)) return false;
        if (!string.Equals(headlessFlag?.Trim(), "1", StringComparison.Ordinal)) return false;
        return hasBatchModeArg;
    }

    public static bool HasBatchModeArg(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, "-batchmode", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
