using System;

namespace Progenitor.Install.Admission;

/// <summary>
/// The image half of the hotLoadable trust rule (<see cref="HotLoadTrust.CleanProgenitorImage"/>): would admission load
/// this image hot on the strength of its declared <c>hotLoadable</c> alone? It runs the same non-executing preflight
/// admission runs (<see cref="ArtifactPreflight.Scan"/>) and accepts one <c>[BepInPlugin]</c> type deriving from
/// <c>ProgenitorPlugin</c>, a resolved base chain and references, and none of the refused findings (UnsafeHarmony, HarmonyLib,
/// ClassInjector, native imports, malformed IL, reflection escapes). Its own file and game-free, so tools/publishcheck
/// links it and tells an author exactly what the Install tab will decide.
/// </summary>
internal static class HotImageCheck
{
    /// <summary><paramref name="reason"/> names the first failed check, or is empty.</summary>
    public static bool IsClean(ReadOnlyMemory<byte> image, out string reason)
    {
        PreflightOutcome scan;
        try { scan = ArtifactPreflight.Scan(image); }
        catch (Exception e)
        {
            reason = "the preflight scan threw: " + e.Message;
            return false;
        }

        if (!scan.Valid) { reason = AdmissionReasonText.For(scan.Refusal ?? AdmissionReasonCode.MalformedImage); return false; }
        if (scan.EntryPoints.Count != 1) { reason = AdmissionReasonText.For(AdmissionReasonCode.MultiplePluginEntryPoints); return false; }
        if (!scan.BaseChainResolved) { reason = AdmissionReasonText.For(AdmissionReasonCode.UnresolvedBaseChain); return false; }
        if (!scan.BaseIsProgenitorPlugin) { reason = AdmissionReasonText.For(AdmissionReasonCode.NotProgenitorPlugin); return false; }
        if (scan.UnresolvedReferences.Count > 0) { reason = AdmissionReasonText.For(AdmissionReasonCode.UnresolvedDependency); return false; }
        AdmissionReasonCode[] refused =
        {
            AdmissionReasonCode.UnsafeHarmonyReference,
            AdmissionReasonCode.HarmonyReference,
            AdmissionReasonCode.ClassInjectorReference,
            AdmissionReasonCode.NativeImport,
            AdmissionReasonCode.MalformedImage,
            AdmissionReasonCode.ReflectionEscape,
        };
        for (int i = 0; i < refused.Length; i++)
        {
            if (!scan.HasFinding(refused[i])) continue;
            reason = AdmissionReasonText.For(refused[i]);
            return false;
        }

        reason = "";
        return true;
    }
}
