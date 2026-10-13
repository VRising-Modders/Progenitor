using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// Mod compatibility C5 (docs/mod-compatibility.md, "Install and Mods pages"; docs/mods-menu.md): the <b>Works across
/// versions</b> marker. A mod gets it only when it declares a <c>Peers</c> range and that range admits a release other than
/// its own version. An undeclared mod (the default same minor), a declared exact version (<c>=1.2.0</c>) or a range that
/// holds nothing but its own version gets no marker. Game-free.
/// </summary>
internal static class ModAcrossVersions
{
    /// <summary>The page's row name, and the heading the list icon's hover text is shown under.</summary>
    internal const string Words = "Works across versions";

    /// <summary>The list readback's <c>acrossVersionsShown</c> values: the icon is drawn, or it is not.</summary>
    internal const string ShownIcon = "icon";

    internal const string ShownNone = "none";

    /// <summary>What the marker means: the list icon's hover text and the page row's text in the description column.</summary>
    internal const string Help =
        "The author says this mod works with its other versions in a declared range, so a server and a player don't need "
        + "the exact same version. The server's version matching decides at the join.";

    /// <summary>
    /// True when <paramref name="declaredPeers"/> is a valid range that admits a release version other than
    /// <paramref name="ownVersion"/>. Empty, invalid or exact-only ranges are false. Prerelease builds are not counted:
    /// <c>&gt;=1.2.0-0 &lt;=1.2.0</c> on 1.2.0 gets no marker.
    /// </summary>
    internal static bool Applies(string? declaredPeers, string? ownVersion)
    {
        string text = (declaredPeers ?? "").Trim();
        if (text.Length == 0 || !CompatRange.TryParse(text, out var range, out _) || range == null) return false;
        if (range.IsAny) return true;
        string own = (ownVersion ?? "").Trim();
        foreach (string candidate in Candidates(text, own))
        {
            if (own.Length > 0 && (CompatVersion.Compare(candidate, own) == 0
                || string.Equals(candidate, own, StringComparison.OrdinalIgnoreCase))) continue;
            if (range.Includes(candidate)) return true;
        }

        return false;
    }

    /// <summary>
    /// Release versions to probe: every version the range names and the own version, each with its parts stepped one up
    /// and one down (later parts kept and zeroed) and with a fourth part, which covers every bound of an interval.
    /// </summary>
    private static List<string> Candidates(string rangeText, string own)
    {
        var bases = new List<List<long>>();
        void AddBase(string token)
        {
            string t = token.TrimStart('>', '<', '=').Trim();
            if (t.Length == 0) return;
            // 1.2.x / 1.x: the wildcard's own release line.
            string[] pieces = t.Split('.');
            if (pieces.Length > 0 && (pieces[pieces.Length - 1] is "x" or "X" or "*"))
                t = string.Join(".", pieces, 0, pieces.Length - 1);
            if (t.Length == 0) return;
            if (CompatVersion.TryParse(t, out var parts, out _)) bases.Add(parts);
        }

        foreach (string alt in rangeText.Split(new[] { "||" }, StringSplitOptions.None))
            foreach (string token in alt.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                AddBase(token);
        if (own.Length > 0) AddBase(own);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(List<long> parts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append('.');
                sb.Append(parts[i].ToString(CultureInfo.InvariantCulture));
            }

            string s = sb.ToString();
            if (seen.Add(s)) result.Add(s);
        }

        foreach (var b in bases)
        {
            var p = new List<long>(b);
            while (p.Count < 3) p.Add(0);
            Add(p);
            for (int i = 0; i < p.Count; i++)
            {
                foreach (long step in new long[] { 1, -1 })
                {
                    if (p[i] + step < 0) continue;
                    var kept = new List<long>(p) { [i] = p[i] + step };
                    Add(kept);
                    var zeroed = new List<long>(kept);
                    for (int j = i + 1; j < zeroed.Count; j++) zeroed[j] = 0;
                    Add(zeroed);
                }
            }

            Add(new List<long>(p) { 1 });
        }

        return result;
    }

    /// <summary>The range in words for the page row: <c>Works with 1.2.0 or newer, below 2.0.0</c>.</summary>
    internal static string RangeWords(string? declaredPeers) => "Works with " + CompatRanges.Words(declaredPeers);
}

/// <summary>The joined server's view of one mod (its feature table row), for the developer details.</summary>
internal sealed class ModDevPeer
{
    /// <summary>The server's version of the mod; null when its table does not list the mod.</summary>
    internal string? Version { get; init; }

    internal IReadOnlyDictionary<string, int> Features { get; init; } = new Dictionary<string, int>();
}

/// <summary>Everything the mod page's developer details are written from. Plain values.</summary>
internal sealed class ModDevFacts
{
    internal string Version { get; init; } = "";

    /// <summary>The build's <c>[NetworkCompatibility]</c>, or null when it has none.</summary>
    internal CompatDeclaration? Declaration { get; init; }

    /// <summary>This process's offered features (name to level).</summary>
    internal IReadOnlyDictionary<string, int> Offers { get; init; } = new Dictionary<string, int>();

    /// <summary>The message router's view this Core generation.</summary>
    internal IReadOnlyList<ModMessageSummary> Messages { get; init; } = Array.Empty<ModMessageSummary>();

    /// <summary>The joined server's table row; null while not connected or before its table arrived.</summary>
    internal ModDevPeer? Peer { get; init; }
}

/// <summary>One developer-details row: its key, name, value and the column's text.</summary>
internal readonly struct ModDevRow
{
    internal ModDevRow(string key, string name, string value, string help)
    {
        Key = key;
        Name = name;
        Value = value;
        Help = help;
    }

    internal string Key { get; }
    internal string Name { get; }
    internal string Value { get; }
    internal string Help { get; }
}

/// <summary>
/// Mod compatibility C5: the mod page's <b>For developers</b> section (collapsed by default): the declared range, the
/// network level and rule, the offered features, the versioned messages and, while connected, the server's copy. The
/// words only; <see cref="ModPagePane"/> draws them in the Details grid. Game-free.
/// </summary>
internal static class ModDevDetails
{
    internal const string Heading = "For developers";

    /// <summary>The section heading's row key (<c>widget_click label=page-section-dev</c>).</summary>
    internal const string HeadingKey = "page-section-dev";

    internal const string NameRange = "Declared range";
    internal const string NameNetwork = "Network";
    internal const string NameFeatures = "Features";
    internal const string NameMessages = "Messages";
    internal const string NamePeer = "Server's copy";

    internal const string NoneOffered = "None offered";
    internal const string NoneRegistered = "None registered";
    internal const string NotDeclared = "Not declared";
    internal const string RuleNotDeclared = "rule not declared";
    internal const string NotOnServer = "Not listed by the server";

    internal const string RangeHelp =
        "The Peers range on this build's [NetworkCompatibility]: which of its own other versions it works with. Without one "
        + "the same minor version applies (or an explicit version rule).";

    internal const string NetworkHelp =
        "The level and version rule on [NetworkCompatibility]: whether the other side must have this mod, and how versions "
        + "compare for peers that do not read ranges.";

    internal const string FeaturesHelp = "Features this mod offers its peers through ctx.Features.Offer, with their levels.";

    internal const string MessagesHelp =
        "Versioned messages this Core generation knows for this mod: the newest version its handler reads and the last "
        + "version it sent with that send's minimum reader. Only handlers registered and messages sent since the last Core "
        + "load are known.";

    internal const string PeerHelp = "The joined server's copy of this mod, from the feature table it sent: its version and features.";

    /// <summary>The rows, top to bottom. The server's row only while its table is known.</summary>
    internal static List<ModDevRow> Rows(ModDevFacts facts)
    {
        facts ??= new ModDevFacts();
        var rows = new List<ModDevRow>(5)
        {
            new("dev-range", NameRange, RangeText(facts.Declaration, facts.Version), RangeHelp),
            new("dev-network", NameNetwork, NetworkText(facts.Declaration), NetworkHelp),
        };
        string features = FeaturesText(facts.Offers);
        rows.Add(new ModDevRow("dev-features", NameFeatures, features, FeaturesHelp + Full(features, NoneOffered)));
        string messages = MessagesText(facts.Messages);
        rows.Add(new ModDevRow("dev-messages", NameMessages, messages, MessagesHelp + Full(messages, NoneRegistered)));
        if (facts.Peer != null)
        {
            string peer = PeerText(facts.Peer);
            rows.Add(new ModDevRow("dev-peer", NamePeer, peer, PeerHelp + Full(peer, NotOnServer)));
        }

        return rows;
    }

    // A long value is clipped in its cell; the column repeats it whole.
    private static string Full(string value, string none) => value == none ? "" : "\n\n" + value;

    /// <summary>
    /// <c>&gt;=1.2.0 &lt;2.0.0 · 1.2.0 or newer, below 2.0.0</c>; <c>None declared (works with 1.4.x by default)</c>;
    /// <c>None declared (exactly 1.4.0, by its Exact rule)</c>; an invalid value says it is ignored.
    /// </summary>
    internal static string RangeText(CompatDeclaration? d, string? version)
    {
        if (d != null && d.DeclaredPeers.Length > 0) return d.DeclaredPeers + " · " + CompatRanges.Words(d.DeclaredPeers);
        string effective = d?.EffectiveRange is { Length: > 0 } e ? e : CompatDeclaration.DefaultRange(version);
        string fallback = d is { RuleDeclared: true }
            ? CompatRanges.Words(effective) + ", by its " + RuleName(d.Rule) + " rule"
            : "works with " + effective + " by default";
        if (effective.Length == 0) fallback = "no range for this version";
        if (d != null && d.PeersInvalid) return "\"" + d.InvalidPeers + "\" is not a valid range and is ignored (" + fallback + ")";
        return "None declared (" + fallback + ")";
    }

    /// <summary><c>ServerMustHave · Exact</c>; <c>NoNeedForSync · rule not declared</c>; <c>Not declared</c>.</summary>
    internal static string NetworkText(CompatDeclaration? d)
    {
        if (d == null) return NotDeclared;
        return LevelName(d.Level) + " · " + (d.RuleDeclared ? RuleName(d.Rule) : RuleNotDeclared);
    }

    internal static string LevelName(NetworkCompatibilityLevel level) => level switch
    {
        NetworkCompatibilityLevel.ServerMustHave => "ServerMustHave",
        NetworkCompatibilityLevel.NoNeedForSync => "NoNeedForSync",
        NetworkCompatibilityLevel.ClientOnly => "ClientOnly",
        _ => ((byte)level).ToString(CultureInfo.InvariantCulture),
    };

    internal static string RuleName(NetworkVersionRule rule) => rule switch
    {
        NetworkVersionRule.Exact => "Exact",
        NetworkVersionRule.Minor => "Minor",
        NetworkVersionRule.None => "None",
        _ => ((byte)rule).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary><c>hello 2 · notes 1</c> (by name), or <c>None offered</c>.</summary>
    internal static string FeaturesText(IReadOnlyDictionary<string, int>? features)
    {
        if (features == null || features.Count == 0) return NoneOffered;
        var names = new List<string>(features.Keys);
        names.Sort(StringComparer.Ordinal);
        var parts = new List<string>(names.Count);
        foreach (string n in names) parts.Add(n + " " + features[n].ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary><c>hello reads v2 · sent v2 (min reader 1)</c>, messages joined by <c>; </c>, or <c>None registered</c>.</summary>
    internal static string MessagesText(IReadOnlyList<ModMessageSummary>? messages)
    {
        if (messages == null || messages.Count == 0) return NoneRegistered;
        var parts = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            string read = m.ReaderVersion > 0 ? "reads v" + m.ReaderVersion.ToString(CultureInfo.InvariantCulture) : "no handler";
            string sent = m.SentVersion > 0
                ? "sent v" + m.SentVersion.ToString(CultureInfo.InvariantCulture)
                    + " (min reader " + m.SentMinReader.ToString(CultureInfo.InvariantCulture) + ")"
                : "not sent";
            parts.Add(m.Name + " " + read + " · " + sent);
        }

        return string.Join("; ", parts);
    }

    /// <summary><c>1.2.0 · hello 2</c>, <c>1.2.0 · no features</c>, or <c>Not listed by the server</c>.</summary>
    internal static string PeerText(ModDevPeer peer)
    {
        if (peer?.Version == null) return NotOnServer;
        string version = peer.Version.Length > 0 ? peer.Version : ModMenuInventory.VersionUnknown;
        return version + " · " + (peer.Features.Count == 0 ? "no features" : FeaturesText(peer.Features));
    }

    /// <summary>
    /// The section heading at the page margin, like Details and About, with the list's fold marker
    /// (<see cref="CellKind.Chevron"/>; it turns when expanded) right after the title rather than at the far right, where
    /// a marker did not read as a fold (the settings drawers' live polish, 2026-10-06). The whole row takes the click.
    /// </summary>
    internal static CellRowSpec HeadingRow()
    {
        float titleWidth = CellLayout.EstimateTextWidth(Heading, ModPageSpacing.HeadingFontSize) + 8f;
        return new CellRowSpec(
            HeadingKey,
            ModPageSpacing.WithLead(new[]
            {
                new CellSpec(CellKind.Text, CellTitle).With(text: Heading, fontSize: ModPageSpacing.HeadingFontSize,
                    vAlign: CellVAlign.Middle, width: CellWidth.Px(titleWidth), minWidth: titleWidth),
                new CellSpec(CellKind.Chevron, CellChevron).With(vAlign: CellVAlign.Middle),
                new CellSpec(CellKind.Spacer, "gap"),
            }),
            height: ModPageSpacing.HeadingHeight, rightInset: HeadBlockLayout.RightColumnInset, soft: true, quietHover: true,
            skin: CellRowSkin.CategoryHeading, gapAbove: ModPageSpacing.HeadingGapAbove);
    }

    internal const string CellTitle = "title";
    internal const string CellChevron = "chevron";
}
