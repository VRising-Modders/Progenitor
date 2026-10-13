using System;

namespace Progenitor.Install.VRising;

/// <summary>
/// The V Rising adapter behind <see cref="CatalogUpdateTags.Adapter"/>: a versioned table
/// from a detected game build (the address-library key the Resilience pass logs as
/// <c>Resilience: game build … build client-247473480-…</c>, i.e. <c>{side}-{build id}</c>)
/// to the Thunderstore update tag that build was published under. This is the only place in
/// the shared Install layer that knows a V Rising patch name; <c>CatalogUpdateTags</c> and the
/// pane only ever see the resolved tag and its source.
///
/// <para>A build with no row resolves to <c>null</c> (unknown), which the pane shows as
/// <c>Current update: unknown</c> and which hides nothing. The table is never derived from the
/// catalog: package-edit timestamps say when an author touched a listing, not which game update
/// is running (job TAG1, handoff install-tab-closure-and-priorities-2026-09-12 §4).</para>
/// </summary>
internal static class VRisingUpdateTagMap
{
    /// <summary>Bumped when a row is added, removed or its tag changes.</summary>
    public const int Version = 1;

    /// <summary>One mapping row; <see cref="Source"/> names the record that pins it.</summary>
    internal readonly struct Row
    {
        public Row(string buildPrefix, string tag, string source)
        {
            BuildPrefix = buildPrefix;
            Tag = tag;
            Source = source;
        }

        /// <summary>
        /// Ordinal-case-insensitive prefix of the build key (<c>{side}-{length}-{hash8}</c> is
        /// enough to pin one build; the full key is accepted too).
        /// </summary>
        public string BuildPrefix { get; }

        /// <summary>The Thunderstore category (<c>&lt;name&gt; Update</c>) for that build.</summary>
        public string Tag { get; }

        /// <summary>Where the mapping was read from (repo path or research note).</summary>
        public string Source { get; }
    }

    /// <summary>
    /// The table. Every row cites the build record it was read from. Only builds this
    /// repository holds a build record for are listed; <c>1.0 Update</c> and
    /// <c>Gloomrot Update</c> have no build id on record here (no fingerprint, address table or
    /// interop archive for those patches exists under <c>tools/addresslib</c> or the
    /// Resilience baselines), so they stay unmapped rather than guessed.
    /// </summary>
    internal static readonly Row[] Rows =
    {
        // The running client. Address table tools/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json;
        // Resilience baseline build-client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json under the client's
        // BepInEx/progenitor-core/addresslib (the Resilience log line "build client-247473480-…" in
        // the Resilience runs of 2026-09-04..06). Thunderstore tags the packages published for it "Oakveil Update".
        new("client-247473480-6a147771", "Oakveil Update",
            "tools/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json; Resilience runs of 2026-09-04..06"),

        // The dedicated server of the same update. Address table tools/addresslib/server-248598344-6a148fd0-ed22e34-3dcd792996235366.json
        // (built with the client table above, tools/addresslib/build.mjs --side server); Resilience baseline
        // build-server-248598344-6a148fd0-ed22e34-3dcd792996235366.json under _testserver/server/BepInEx/progenitor-core/addresslib.
        new("server-248598344-6a148fd0", "Oakveil Update",
            "tools/addresslib/server-248598344-6a148fd0-ed22e34-3dcd792996235366.json; _testserver Resilience baseline"),
    };

    /// <summary>
    /// The update tag for a build key, or <c>null</c> when the build is empty or has no row.
    /// The client and the server of one update carry different build ids, so both are listed.
    /// </summary>
    public static string? TagForBuild(string? buildKey)
    {
        if (string.IsNullOrWhiteSpace(buildKey)) return null;
        string key = buildKey.Trim();
        for (int i = 0; i < Rows.Length; i++)
        {
            if (key.StartsWith(Rows[i].BuildPrefix, StringComparison.OrdinalIgnoreCase)) return Rows[i].Tag;
        }

        return null;
    }
}
