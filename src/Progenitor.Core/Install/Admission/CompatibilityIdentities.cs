using System;
using System.Collections.Generic;

namespace Progenitor.Install.Admission;

/// <summary>
/// HOT2: the three compatibility identities a <see cref="QualificationRecord"/> is keyed by, resolved
/// from this process. A record is a claim about one exact image on one exact game build against one
/// exact Bootstrap/Core pairing; without these three values there is nothing for it to match, which
/// is why every production admission before HOT2 could only be
/// <see cref="AdmissionReasonCode.NoQualificationRecord"/> or
/// <see cref="AdmissionReasonCode.QualificationMismatch"/>.
///
/// <para><b>Unknown is spelled out, not omitted.</b> A missing value is the literal
/// <see cref="Unknown"/>, never "". <see cref="QualificationRecord.Mismatches"/> treats a blank on
/// either side as a mismatch, so an unresolved build cannot accidentally satisfy a record that also
/// left the field blank — and a record authored against <c>unknown</c> says in the file that that is
/// what it was reviewed against.</para>
/// </summary>
internal static class CompatibilityIdentities
{
    /// <summary>The value used when a source cannot answer. Compared like any other string.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The game build key, from the Resilience pass's fingerprint
    /// (<c>client-247473480</c>-shaped) via the same provider the update-tag resolver reads
    /// (<see cref="CatalogUpdateTags.BuildProvider"/>). Null/blank → <see cref="Unknown"/>: the pass
    /// has not run yet in this process.
    /// </summary>
    public static string GameBuild()
    {
        try
        {
            var provider = CatalogUpdateTags.BuildProvider;
            string? value = provider?.Invoke();
            return string.IsNullOrWhiteSpace(value) ? Unknown : value!.Trim();
        }
        catch
        {
            return Unknown;
        }
    }

    /// <summary>
    /// The Bootstrap contract: the Core/Bootstrap install handshake number Core insists on
    /// (<see cref="BootstrapLink.ProtocolVersion"/>) together with the number Bootstrap published.
    /// They are equal on any pairing that loaded at all — a mismatch refuses the Core load — but
    /// recording both makes a record readable without knowing which side wrote it, and
    /// <c>absent</c> names the no-Bootstrap case rather than inventing a number for it.
    /// </summary>
    public static string BootstrapContract()
    {
#if PROGENITOR_TESTS
        // The test build does not compile BootstrapLink; there is no Bootstrap in a test process
        // either, so the honest answer is the same one an absent Bootstrap gets.
        return Unknown;
#else
        int host;
        try { host = BootstrapLink.ReadBootstrapProtocolOrAbsent(); }
        catch { return Unknown; }
        string published = host < 0 ? "absent" : host.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "protocol/" + BootstrapLink.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "+host/" + published;
#endif
    }

    /// <summary>
    /// The Core contract: Core's plugin version plus the admission policy version. The policy
    /// version is part of it on purpose — a change to the admission state machine changes what a
    /// record means, and an old record must not carry over silently.
    /// </summary>
    public static string CoreContract()
    {
        string version;
#if PROGENITOR_TESTS
        version = Unknown;
#else
        try { version = Plugin.Version; }
        catch { version = Unknown; }
#endif
        if (string.IsNullOrWhiteSpace(version)) version = Unknown;
        return "core/" + version.Trim() + "+" + AdmissionEvaluator.PolicyVersion;
    }

    /// <summary>The three identities, in the order the record file lists them.</summary>
    public static IReadOnlyList<CompatibilityIdentity> Current() => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, GameBuild()),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, BootstrapContract()),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, CoreContract()),
    };
}
