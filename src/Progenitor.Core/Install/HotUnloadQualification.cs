using System;
using System.Collections.Generic;
using Progenitor.Install.Admission;

namespace Progenitor.Install;

/// <summary>
/// POLISH1 defect D: does the local reviewed policy cover the exact bytes of an installed image for
/// same-session activation on one side? This is the only question the Install tab asks about a
/// <em>loaded</em> ordinary install, and it is what decides whether removing it costs a restart.
///
/// <para>Why the question is the right one. After a restart a Progenitor mod that was hot loaded in
/// an earlier session is just a <c>progenitor-reload/</c> file the reloader picked up at start, and
/// the row cannot tell it apart from a promoted third-party mod by the file alone — which is exactly
/// why <see cref="HotLoadExecutor"/>'s own ledger flag is cleared at every start (INSTALL2 defect A).
/// A reviewed record for those bytes on this side is the standing evidence that they are a
/// collectible plugin the reloader may drop in process: the same record
/// <see cref="AdmissionEvaluator"/> requires before it will let the executor write them at all.
/// Live (deploy #70) <c>Progenitor-HotDemo</c> read <c>restart true</c> after a restart and its
/// Uninstall owed a restart it did not owe.</para>
///
/// <para>What is checked, and what is deliberately not. The plugin id, the artifact sha256 and size,
/// the operation, the target and the three compatibility identities
/// (<see cref="CompatibilityIdentities"/>) must all match, exactly as
/// <see cref="QualificationRecord.Mismatches"/> compares them — a missing or zero size, an empty
/// plugin id and an empty policy all refuse. The record's <see cref="DependencyClosure"/> is not
/// compared, because it keys the zip's bundled images at the moment of the write and cannot be
/// rebuilt from the one installed DLL; nothing here authorizes a write, so the closure has nothing to
/// protect. This never admits anything: it only tells a row whether an unload would cost a restart.</para>
/// </summary>
internal static class HotUnloadQualification
{
    /// <summary>
    /// Is there a record in <paramref name="policy"/> for <paramref name="pluginId"/> that covers
    /// these exact bytes, this <paramref name="target"/> and the current compatibility identities?
    /// </summary>
    /// <param name="identities">
    /// The three identities to compare against; <see cref="CompatibilityIdentities.Current"/> when
    /// null. A test passes its own so it never depends on the process it runs in.
    /// </param>
    internal static bool Covers(
        QualificationPolicy? policy,
        string? pluginId,
        string? sha256,
        long size,
        InstallTarget target,
        IReadOnlyList<CompatibilityIdentity>? identities = null)
    {
        if (policy == null) return false;
        string id = (pluginId ?? "").Trim();
        string sha = (sha256 ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0 || size <= 0 || !QualificationPolicy.IsSha256(sha)) return false;

        var ids = identities ?? CompatibilityIdentities.Current();
        string build = Identity(ids, CompatibilityKeys.GameBuild);
        string bootstrap = Identity(ids, CompatibilityKeys.BootstrapContract);
        string core = Identity(ids, CompatibilityKeys.CoreContract);
        var artifact = new ArtifactIdentity(sha, size);

        var records = policy.For(id);
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Operation != AdmissionOperation.InitialInstall) continue;
            if (!record.Artifact.Matches(artifact)) continue;
            if (!QualificationRecord.TargetCovers(record.Target, target)) continue;
            if (!Same(record.GameBuild, build)) continue;
            if (!Same(record.BootstrapContract, bootstrap)) continue;
            if (!Same(record.CoreContract, core)) continue;
            return true;
        }

        return false;
    }

    /// <summary>The same blank-is-never-a-match comparison <see cref="QualificationRecord"/> uses.</summary>
    private static bool Same(string recorded, string? actual) =>
        !string.IsNullOrWhiteSpace(recorded)
        && !string.IsNullOrWhiteSpace(actual)
        && string.Equals(recorded.Trim(), actual!.Trim(), StringComparison.Ordinal);

    private static string Identity(IReadOnlyList<CompatibilityIdentity> identities, string name)
    {
        for (int i = 0; i < identities.Count; i++)
        {
            if (string.Equals(identities[i].Name, name, StringComparison.Ordinal)) return identities[i].Value ?? "";
        }

        return "";
    }
}
