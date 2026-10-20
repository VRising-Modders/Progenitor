namespace Progenitor.Install;

/// <summary>
/// The release keys built into Core: the public halves (SPKI DER, base64) of the ECDSA P-256 keys that sign Progenitor's
/// release list (<see cref="SelfFeed"/>). A feed is accepted when one of its signatures verifies with <see cref="Current"/>
/// or <see cref="Next"/>, so the key can be rolled: put the new key in <see cref="Next"/>, ship a release, sign with both,
/// then move it to <see cref="Current"/>.
/// <para><c>node tools/release-feed/release-feed.mjs keygen</c> writes these lines; the private keys never enter the repository
/// (docs/releasing.md). An empty slot is no key; with both empty no release list verifies and Progenitor offers no update
/// of itself.</para>
/// </summary>
internal static class SelfFeedKeys
{
    // release-feed:current
    public static readonly string Current = "";

    // release-feed:next
    public static readonly string Next = "";

    /// <summary>The keys a feed may be signed with (empty slots left out).</summary>
    public static string[] Trusted()
    {
        if (Current.Length > 0 && Next.Length > 0) return new[] { Current, Next };
        if (Current.Length > 0) return new[] { Current };
        if (Next.Length > 0) return new[] { Next };
        return System.Array.Empty<string>();
    }
}
