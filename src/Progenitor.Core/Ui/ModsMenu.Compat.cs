using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// Mod compatibility C5: the evidence behind the Works across versions marker (the list entry and the page row) and the
/// mod page's developer details. The words and the decisions are in <see cref="ModAcrossVersions"/> and
/// <see cref="ModDevDetails"/>; this file only gathers plain values.
/// </summary>
public static partial class ModsMenu
{
    // ---- the declared range -----------------------------------------------------------------------

    /// <summary>Tests: the declared <c>Peers</c> of an inspected package version (package key, version), or null.</summary>
    internal static Func<string, string, string?>? InspectedPeersProbe { get; set; }

    private static readonly Dictionary<string, string> InspectedPeers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>Peers</c> range <paramref name="pluginId"/> declares at <paramref name="version"/>, or empty. The loaded copy's
    /// attribute (<see cref="ModRegistryEntry.Compat"/>) counts only when its version is the one shown; otherwise an
    /// installed copy's verified inspection (<c>progenitor-cache/verified</c>, the ledger's package at that version) when
    /// its <c>progenitor.json</c> names this plugin. Nothing else is guessed: the Thunderstore index carries no ranges.
    /// </summary>
    internal static string DeclaredPeersOf(string pluginId, ModsSidebarRow? row, string version)
    {
        try
        {
            string v = (version ?? "").Trim();
            if (ModRegistry.TryGet(pluginId, out var entry) && SameVersion(entry.Version, v))
                return entry.Compat?.DeclaredPeers ?? "";
            if (row == null || row.Provenance == ModProvenance.Server) return "";
            foreach (var ledger in row.Ledger)
            {
                if (string.Equals(ledger.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!InstalledState.TrySplitPackageSource(ledger.Source, out string package, out string at)) continue;
                string ver = at.Length > 0 ? at : ledger.Version;
                if (!SameVersion(ver, v)) continue;
                string peers = InspectedPeersOf(pluginId, package, ver);
                if (peers.Length > 0) return peers;
            }
        }
        catch { /* nothing known */ }

        return "";
    }

    private static bool SameVersion(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        || CompatVersion.Compare(a, b) == 0;

    /// <summary>The declared range of an inspected package version, remembered per package version (an inspection never changes).</summary>
    private static string InspectedPeersOf(string pluginId, string package, string version)
    {
        var probe = InspectedPeersProbe;
        if (probe != null) return (probe(package, version) ?? "").Trim();
        string key = pluginId + "|" + package + "|" + version;
        return InspectedPeersCached(key, () =>
        {
            string? root = ClientUpdates.BepInExRootProbe?.Invoke();
            int dash = package.LastIndexOf('-');
            if (string.IsNullOrEmpty(root) || dash <= 0 || dash >= package.Length - 1) return (false, "");
            var record = new VerifiedManifestCache(root!).Find(package.Substring(0, dash), package.Substring(dash + 1), version);
            if (record == null) return (false, "");
            return (true, string.Equals(record.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase) ? (record.Manifest.Peers ?? "").Trim() : "");
        });
    }

    /// <summary>
    /// The remembered answer for <paramref name="key"/>, else <paramref name="lookup"/>'s. Only a found inspection is
    /// remembered (an inspection never changes); a miss is asked again next time, so the marker appears once the package's
    /// inspection is written.
    /// </summary>
    internal static string InspectedPeersCached(string key, Func<(bool Found, string Peers)> lookup)
    {
        lock (InspectedPeers)
            if (InspectedPeers.TryGetValue(key, out var known)) return known;
        var (found, peers) = lookup();
        if (!found) return "";
        lock (InspectedPeers)
        {
            if (InspectedPeers.Count >= 512) InspectedPeers.Clear();
            InspectedPeers[key] = peers;
        }

        return peers;
    }

    // ---- the developer details --------------------------------------------------------------------

    /// <summary>The mods whose developer details are expanded (kept while the workspace is open).</summary>
    private static readonly HashSet<string> DevExpanded = new(StringComparer.OrdinalIgnoreCase);

    internal static bool DevDetailsExpanded(string pluginId) => DevExpanded.Contains(pluginId ?? "");

    /// <summary>Tests: the network half of the developer details (offers, messages, the server's row) for a mod id.</summary>
    internal static Func<string, (IReadOnlyDictionary<string, int> Offers, IReadOnlyList<ModMessageSummary> Messages, ModDevPeer? Peer)>?
        DevNetProbe { get; set; }

    /// <summary>
    /// The developer details of a Progenitor plugin loaded on this side (it has a registry entry), else null: other mods
    /// show no section.
    /// </summary>
    internal static ModDevFacts? DevFactsOf(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !ModRegistry.TryGet(pluginId, out var entry)) return null;
        IReadOnlyDictionary<string, int> offers = new Dictionary<string, int>();
        IReadOnlyList<ModMessageSummary> messages = Array.Empty<ModMessageSummary>();
        ModDevPeer? peer = null;
        try
        {
            var probe = DevNetProbe;
            if (probe != null) (offers, messages, peer) = probe(pluginId);
#if !PROGENITOR_TESTS
            else
            {
                offers = ModCompatNet.Offers.Of(pluginId);
                messages = ModCompatNet.Router.Describe(pluginId);
                // Client: the joined server's table (absent while not connected or before it arrived).
                var table = ModCompatNet.PeerTables.Get(ModCompatNet.ServerPeer);
                if (table != null)
                {
                    peer = table.Mods.TryGetValue(pluginId, out var row)
                        ? new ModDevPeer { Version = row.Version, Features = new Dictionary<string, int>(row.Features) }
                        : new ModDevPeer();
                }
            }
#endif
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: developer details of " + pluginId + " threw: " + e.Message); }

        return new ModDevFacts
        {
            Version = entry.Version,
            Declaration = entry.Compat,
            Offers = offers,
            Messages = messages,
            Peer = peer,
        };
    }

    /// <summary>The section heading's click: fold or unfold this mod's developer details and redraw the page in place.</summary>
    internal static void OnDevDetailsToggle(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;
        if (!DevExpanded.Remove(pluginId)) DevExpanded.Add(pluginId);
        if (!IsOpen || !string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase)) return;
        RebuildModKeepingScroll(pluginId, "dev-details");
    }

    /// <summary>The workspace closed: the sections fold again next time.</summary>
    private static void ForgetDevDetails() => DevExpanded.Clear();

    internal static void ResetCompatDetailsForTests()
    {
        DevExpanded.Clear();
        DevNetProbe = null;
        InspectedPeersProbe = null;
        lock (InspectedPeers) InspectedPeers.Clear();
    }
}
