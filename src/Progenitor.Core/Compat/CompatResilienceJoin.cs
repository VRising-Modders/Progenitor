using System;
using BepInEx;
using Progenitor.Resilience;

namespace Progenitor.Compat;

/// <summary>
/// The one wire between the two halves of M4 (job FCOMPAT3): the resilience report says
/// <i>what broke</i>, the drift map says <i>what of it can be absorbed</i>, and this points the
/// report's join at the map for the running build.
///
/// Deliberately <b>not</b> mirrored into Bootstrap and deliberately not part of the join itself.
/// The join lives in <see cref="ResilienceDiff.JoinDriftMap"/> and is pure — a diff and a map,
/// no file system, no game — which is what lets <c>ResilienceTests</c> and
/// <c>tools/resilience-report.ps1</c> drive it offline. Everything that needs a running install
/// (the BepInEx root, the build id of <c>GameAssembly.dll</c>, which side this is) is here, in
/// the one file that is not compiled by the tests.
/// </summary>
public static class CompatResilienceJoin
{
    /// <summary>
    /// Point <see cref="ResilienceDiff.DriftMapProvider"/> at the running build's map. The map
    /// itself is resolved and cached once per process by <see cref="CompatRewriteHost.Map"/>, so
    /// the resilience pass (a background thread) shares the load path's map rather than reading
    /// and parsing a second copy — and, in particular, shares its allowlist verdict.
    /// </summary>
    public static void Install()
    {
        ResilienceDiff.DriftMapProvider = MapForThisBuild;
    }

    /// <summary>Drop the provider. A Core generation must not leave a delegate behind.</summary>
    public static void Uninstall()
    {
        ResilienceDiff.DriftMapProvider = null;
    }

    /// <summary>The drift map for the build now running, or null. Never throws.</summary>
    public static DriftMap? MapForThisBuild()
    {
        try
        {
            string root = Paths.BepInExRootPath ?? "";
            if (root.Length == 0) return null;
            string build = CompatRewriteHost.TryComputeBuildId(CompatRewriteHost.GameAssemblyPath(root));
            if (build.Length == 0) return null;
            return CompatRewriteHost.Map(
                CompatRewriteHost.CoreFolder(root), CompatRewriteHost.DetectSide(), build, out _, out _);
        }
        catch
        {
            return null;
        }
    }
}
