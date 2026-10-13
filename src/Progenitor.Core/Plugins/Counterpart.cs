using System;
using System.Collections.Generic;

namespace Progenitor.Plugins;

/// <summary>
/// How much of a mod still works when its counterpart — the other side's part of the same mod — is
/// missing: a client mod on a server that does not run its server part, or a server mod for a
/// player who has not installed its client part. Declared with <see cref="CounterpartAttribute"/>.
/// </summary>
public enum CounterpartNeed : byte
{
    /// <summary>
    /// Nothing works without the counterpart. The mod keeps today's behavior: with
    /// <c>[NetworkCompatibility(ServerMustHave)]</c> the join is refused, or a hot-loaded client mod
    /// is paused for that server (docs/mods-menu.md, "Per-server mod pause").
    /// </summary>
    Required = 0,

    /// <summary>
    /// The mod runs without its counterpart, with some features unavailable. It is never paused for
    /// a server that lacks the counterpart: it keeps running, the settings rows marked
    /// <c>needsServerPart</c> are grayed with their fallback, and the mod page says
    /// <c>Server part not on this server · limited</c>.
    /// </summary>
    Limited = 1,

    /// <summary>
    /// The mod works fully without its counterpart; the counterpart only adds extras (for example a
    /// server mod with an optional client part, described by <see cref="CounterpartAttribute.ClientExtras"/>).
    /// Never paused, and nothing is grayed.
    /// </summary>
    Optional = 2,
}

/// <summary>
/// Declares how this mod relates to its counterpart on the other side. Read from the plugin type
/// when it loads, beside <see cref="NetworkCompatibilityAttribute"/>.
/// </summary>
/// <remarks>
/// <para><b>Handshake.</b> A mod that runs without its counterpart cannot also demand it: when this
/// attribute says <see cref="CounterpartNeed.Limited"/> or <see cref="CounterpartNeed.Optional"/>
/// and <c>[NetworkCompatibility]</c> says <see cref="NetworkCompatibilityLevel.ServerMustHave"/>,
/// the handshake row is declared <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> instead (the
/// version rule is kept, so two sides that both run the mod still have to agree on its version).
/// The load log says so once.</para>
/// <para><b>Per-server pause.</b> Because of that row, a mod that declares it runs without its
/// counterpart is never paused and never refused for a server that lacks it.</para>
/// <para><b>Which features need the counterpart.</b> <see cref="Features"/> names them in plain
/// words for the mod page; mark the matching settings rows with <c>needsServerPart: true</c> and a
/// <c>fallback</c> on <c>ctx.Settings.Page</c> so they are grayed with that fallback.</para>
/// </remarks>
/// <example>
/// <code>
/// [BepInPlugin("example.minimap", "Minimap", "1.0.0")]
/// [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
/// [Counterpart(CounterpartNeed.Limited, Features = new[] { "Clan markers" })]
/// public sealed class Plugin : ProgenitorPlugin&lt;PluginContext&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class CounterpartAttribute : Attribute
{
    /// <param name="withoutCounterpart">How much still works when the other side's part is missing.</param>
    public CounterpartAttribute(CounterpartNeed withoutCounterpart)
    {
        WithoutCounterpart = withoutCounterpart;
    }

    /// <summary>How much still works when the other side's part is missing.</summary>
    public CounterpartNeed WithoutCounterpart { get; }

    /// <summary>The features that need the counterpart, in plain words (shown on the mod page).</summary>
    public string[] Features { get; set; } = Array.Empty<string>();

    /// <summary>A server mod: what an optional client part adds, in one plain sentence. Empty for none.</summary>
    public string ClientExtras { get; set; } = "";
}

/// <summary>
/// What a client knows about its mod's server part on the joined server
/// (<c>ctx.Handshake.ServerPart</c>). The evidence is the server's handshake request, the same
/// evidence the mod page's status bar and its <c>needsServerPart</c> rows read.
/// </summary>
public enum CounterpartPresence : byte
{
    /// <summary>
    /// Nothing proves it either way: not joined, the handshake request has not arrived, the server
    /// does not run Progenitor, or its request was full (16 rows) and may have left the mod out.
    /// </summary>
    Unknown = 0,

    /// <summary>The joined server's handshake request named this mod: its server part is running there.</summary>
    Running = 1,

    /// <summary>The joined server sent its full handshake request (fewer than 16 rows) and did not name this mod.</summary>
    Absent = 2,
}

/// <summary>One plugin's counterpart declaration, as plain data (no attribute instance is kept).</summary>
internal sealed class CounterpartDeclaration
{
    internal CounterpartDeclaration(CounterpartNeed need, IReadOnlyList<string>? features = null, string? clientExtras = null)
    {
        Need = need;
        var list = new List<string>();
        if (features != null)
            foreach (var f in features)
                if (!string.IsNullOrWhiteSpace(f)) list.Add(f.Trim());
        Features = list;
        ClientExtras = (clientExtras ?? "").Trim();
    }

    internal CounterpartNeed Need { get; }

    internal IReadOnlyList<string> Features { get; }

    internal string ClientExtras { get; }

    /// <summary>True when the mod keeps running without its counterpart (<c>Limited</c> or <c>Optional</c>).</summary>
    internal bool RunsWithout => CounterpartPolicy.RunsWithout(Need);

    internal static CounterpartDeclaration? From(CounterpartAttribute? attribute) =>
        attribute == null ? null : new CounterpartDeclaration(attribute.WithoutCounterpart, attribute.Features, attribute.ClientExtras);
}

/// <summary>The pure rules of <see cref="CounterpartAttribute"/>. Game-free.</summary>
internal static class CounterpartPolicy
{
    /// <summary>The word appended to the server-part cell when a mod runs without its counterpart.</summary>
    internal const string LimitedWord = "limited";

    /// <summary>True for <c>Limited</c> and <c>Optional</c>: the mod keeps running without its counterpart.</summary>
    internal static bool RunsWithout(CounterpartNeed? need) =>
        need == CounterpartNeed.Limited || need == CounterpartNeed.Optional;

    /// <summary>
    /// The handshake level a plugin actually declares. A mod that runs without its counterpart
    /// cannot demand it, so <c>ServerMustHave</c> becomes <c>NoNeedForSync</c>; every other
    /// combination is unchanged.
    /// </summary>
    internal static NetworkCompatibilityLevel EffectiveLevel(NetworkCompatibilityLevel declared, CounterpartNeed? need) =>
        EffectiveLevel(declared, need, isServer: false, clientExtras: null);

    /// <summary>
    /// Review of 5a6e40f8, finding 4: the declaration is directional. On the <b>client</b> it speaks
    /// about the server part: <c>Limited</c> / <c>Optional</c> lower the client's own row, so the client
    /// is not refused or paused for a server without it. On the <b>server</b> it speaks about the
    /// client part, and the server lowers its requirement that clients have the mod only when the
    /// server mod says the client part is optional (<c>Optional</c>, or <c>ClientExtras</c> set).
    /// </summary>
    internal static NetworkCompatibilityLevel EffectiveLevel(
        NetworkCompatibilityLevel declared, CounterpartNeed? need, bool isServer, string? clientExtras)
    {
        if (declared != NetworkCompatibilityLevel.ServerMustHave || need == null) return declared;
        bool lower = isServer
            ? need == CounterpartNeed.Optional || !string.IsNullOrWhiteSpace(clientExtras)
            : RunsWithout(need);
        return lower ? NetworkCompatibilityLevel.NoNeedForSync : declared;
    }

    /// <summary>
    /// The client's view of plugin <paramref name="guid"/>'s server part, from the rows the joined
    /// server's handshake request carried (<paramref name="asked"/>, metadata row stripped; empty when
    /// there was none). Named with a version: <see cref="CounterpartPresence.Running"/>. Not named in a
    /// request that proves absence (<see cref="RequestProvesAbsence"/>):
    /// <see cref="CounterpartPresence.Absent"/>. Anything else: <see cref="CounterpartPresence.Unknown"/>.
    /// </summary>
    /// <param name="truncated">The request filled every wire slot (<c>Handshake.ServerRequestTruncated</c>).</param>
    internal static CounterpartPresence ServerPartFrom(
        IReadOnlyList<Progenitor.Networking.Handshake.ModRecord>? asked, string? guid, bool truncated)
    {
        if (asked == null || asked.Count == 0 || string.IsNullOrWhiteSpace(guid)) return CounterpartPresence.Unknown;
        for (int i = 0; i < asked.Count; i++)
        {
            var row = asked[i];
            if (string.Equals(row.Guid, guid, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(row.Version))
                return CounterpartPresence.Running;
        }
        return RequestProvesAbsence(asked.Count, truncated) ? CounterpartPresence.Absent : CounterpartPresence.Unknown;
    }

    /// <summary>
    /// A request proves that a mod it does not name is absent only when it carried rows, did not fill
    /// every wire slot (<paramref name="truncated"/>, taken before the metadata row is stripped: a full
    /// 16-slot request keeps 15 rows once it is) and kept fewer rows than the slots. Used by the mod
    /// page and by <c>ctx.Handshake.ServerPart</c>, so the two cannot disagree.
    /// </summary>
    internal static bool RequestProvesAbsence(int keptRows, bool truncated) =>
        keptRows > 0 && !truncated && keptRows < Progenitor.Networking.Handshake.MaxMods;

    /// <summary>The load-log line when the level was lowered.</summary>
    internal static string LoweredLog(string name, CounterpartNeed need) =>
        name + ": [Counterpart(" + need + ")] runs without its counterpart, so its handshake row is declared "
        + "NoNeedForSync instead of ServerMustHave.";
}
