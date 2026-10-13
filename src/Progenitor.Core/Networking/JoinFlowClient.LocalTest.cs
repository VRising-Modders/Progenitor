using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// LOCALJOIN1 (debug, requested 2026-10-07): the join window's local test source. A required mod the
/// server named no usable source for is offered from the configured local test index
/// (<c>[Install] LocalIndexPath</c>, HOT1) and installed through the join's usual lane. Nothing changes while
/// the key is empty, and a source the server named is never replaced while it is usable.
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>
    /// The configured local test index's folder, or null (the default for everyone). Production:
    /// <c>LocalIndexConfig.Resolve</c>; tests set it.
    /// </summary>
    internal static Func<string?>? LocalTestFolderProbe { get; set; }

    /// <summary>Tests: what a local zip declares. Null reads the zip.</summary>
    internal static Func<string, Install.LocalZipFacts>? LocalTestGuidsProbe { get; set; }

    /// <summary>The local test folder this build used (readback), or empty.</summary>
    internal static string LocalTestFolderNow
    {
        get
        {
            try { return LocalTestFolderProbe?.Invoke() ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>The configured local test index provides <paramref name="id"/> (false while none is configured).</summary>
    internal static bool LocalTestProvides(string id)
    {
        var lookup = LocalTestOfForBuild();
        return lookup != null && lookup(id) != null;
    }

    /// <summary>GUID → the local listing that provides it; empty without a local index.</summary>
    private static Dictionary<string, Install.LocalTestMatch> LocalTestMap()
    {
        string folder = LocalTestFolderNow;
        if (folder.Trim().Length == 0) return new Dictionary<string, Install.LocalTestMatch>();
        try { return Install.LocalTestSource.Map(folder, LocalTestGuidsProbe); }
        catch { return new Dictionary<string, Install.LocalTestMatch>(); }
    }

    /// <summary>
    /// SELFFIX1: the required mods this client can fix by itself during the join, for its mod report: a copy the player
    /// turned off that turns on now (the join's own Turn on conditions), and — only with <c>[Install] LocalIndexPath</c>
    /// set — a hot-loadable package its local test source offers. Bounded; the server treats every claim as untrusted.
    /// </summary>
    internal static IReadOnlyList<ClientModFix> SelfFixClaims() => SelfFixClaims(CurrentRequiredRows());

    /// <summary>
    /// Review (MEDIUM-LOW): the rows a claim may name: this join's own handshake request (the server's asked rows, read
    /// fresh each report) together with the remembered policy's required rows. Null only when neither is known (a first
    /// join to a server that asks nothing yet): every candidate is then claimed, bounded.
    /// </summary>
    internal static IReadOnlyList<ClientModRow>? CurrentRequiredRows()
    {
        var rows = new List<ClientModRow>();
        bool known = false;
        IReadOnlyList<string>? asked;
        try { asked = (AskedGuidsSource ?? LiveAskedGuids)(); }
        catch { asked = null; }
        if (asked != null && asked.Count > 0)
        {
            known = true;
            foreach (string id in asked)
                if (!string.IsNullOrWhiteSpace(id)) rows.Add(new ClientModRow(id, "", "", ClientModRule.AtLeast));
        }

        var remembered = RememberedRequiredProbe();
        if (remembered != null)
        {
            known = true;
            rows.AddRange(remembered);
        }

        return known ? rows : null;
    }

    /// <summary>This join's asked plugin GUIDs (live: the handshake request this connection received). Tests set it.</summary>
    internal static Func<IReadOnlyList<string>>? AskedGuidsSource { get; set; }

    private static IReadOnlyList<string> LiveAskedGuids()
    {
        var list = new List<string>();
        foreach (var r in Handshake.CopyServerAsked())
            if (!string.IsNullOrEmpty(r.Guid) && !string.Equals(r.Guid, Handshake.CapabilityGuid, StringComparison.OrdinalIgnoreCase)) list.Add(r.Guid);
        return list;
    }

    /// <summary>
    /// Review (MEDIUM-LOW): local test source claims beyond the known rows, at most this many. A requirement the admins
    /// added since the remembered policy is in neither source until the server answers this join, and the local test
    /// source is a debug folder the player configured (not their installed mods), so a few such claims are sent anyway;
    /// the server ignores a claim for a row it does not require.
    /// </summary>
    internal const int MaxUnfilteredLocalClaims = 4;

    /// <summary>
    /// SELFFIX1 review: the remembered policy's required rows for the server being joined (live:
    /// <c>ServerModPause.RememberedRequiredRows</c>), or null when none is remembered. Tests set it.
    /// </summary>
    internal static Func<IReadOnlyList<ClientModRow>?>? RememberedRequiredSource { get; set; }

    private static IReadOnlyList<ClientModRow>? RememberedRequiredProbe()
    {
        try { return RememberedRequiredSource?.Invoke(); }
        catch { return null; }
    }

    /// <summary>
    /// The claims for <paramref name="required"/>: keys those rows name, at the version each row's rule accepts (review:
    /// claims reveal no more of this client's mods than the server asks for, and a turned-off mod never crowds out a
    /// local-test claim), plus at most <see cref="MaxUnfilteredLocalClaims"/> local test source claims outside them. With no
    /// rows known (null) every candidate is claimed, newest version, within <see cref="ClientModFix.MaxFixes"/>.
    /// </summary>
    internal static IReadOnlyList<ClientModFix> SelfFixClaims(IReadOnlyList<ClientModRow>? required)
    {
        var list = new List<ClientModFix>();
        try
        {
            ClientModRow? RowFor(string id)
            {
                if (required == null) return null;
                foreach (var r in required) if (r.Names(id)) return r;
                return null;
            }

            bool Wanted(string id) => required == null || RowFor(id) != null;

            var offProbe = OffCopyProbe;
            if (offProbe != null)
                foreach (var e in Plugins.ModEnableState.Disabled())
                {
                    if (list.Count >= ClientModFix.MaxFixes) break;
                    if (!Wanted(e.Id)) continue;
                    JoinOffCopy? off;
                    try { off = offProbe(e.Id); }
                    catch { off = null; }
                    if (off is { PausedHere: false, AppliesNow: true, TurnsOnAtNextStart: false })
                        list.Add(new ClientModFix(e.Id, ClientModFix.KindTurnOn, off.Version));
                }

            int extras = 0;
            foreach (var pair in LocalTestMap())
            {
                if (list.Count >= ClientModFix.MaxFixes) break;
                if (!pair.Value.HotLoadable) continue;
                if (!Wanted(pair.Key))
                {
                    // Not in the known rows: a bounded claim at the newest version (see MaxUnfilteredLocalClaims).
                    if (extras >= MaxUnfilteredLocalClaims) continue;
                    extras++;
                }

                var row = RowFor(pair.Key);
                string version = pair.Value.Version;
                if (row != null && row.Version.Length > 0)
                {
                    string? pick = Install.ThunderstoreVersions.Resolve(pair.Value.Active, row.Version,
                        InstallWindowFixes.LookupRule(row.Rule), out _);
                    if (pick == null) continue; // the local source has no version this server accepts
                    version = pick;
                }

                list.Add(new ClientModFix(pair.Key, ClientModFix.KindLocalTest, version));
            }
        }
        catch { /* a claim is a hint; none is sent */ }

        return list;
    }

    /// <summary>
    /// SELFFIX1 review: the local test source's version of <paramref name="guid"/> that the server's own check accepts
    /// (any of its active versions, newest first), or null.
    /// </summary>
    internal static string? LocalTestVersionAccepted(string guid, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided,
        Install.LocalTestMatch local)
    {
        var versions = new List<string>(local.Active);
        versions.Sort((a, b) => JoinPageModel.CompareVersions(b, a));
        foreach (string v in versions)
            if (JoinPageModel.OffCopySatisfies(guid, new JoinOffCopy { Version = v }, asked, provided)) return v;
        return null;
    }

    /// <summary>One lookup per model build: GUID → the local listing that provides it; null without a local index.</summary>
    private static Func<string, Install.LocalTestMatch?>? LocalTestOfForBuild()
    {
        var map = LocalTestMap();
        if (map.Count == 0) return null;
        return id => !string.IsNullOrEmpty(id) && map.TryGetValue(id, out var m) ? m : null;
    }
}
