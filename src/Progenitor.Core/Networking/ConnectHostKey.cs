using System;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>
/// HOSTKEY1 (docs/mod-manifest.md, "Rows written under relay strings"): which address names the server this client is
/// joining, for the trust pins, the Thunderstore confirmations and the server profiles. Game-free.
///
/// <para>Live 2026-10-05: <c>ClientBootstrapSystem.ConnectionString</c> of a SteamIPv4 join is a base64 Steam relay
/// string whose middle bytes change on every connection (<c>Aqf27THwTQgI4Hk6FpSBAvJNEAA</c>,
/// <c>Aqf27THwTQgIuB3hF5SBAvJNEAA</c>, … for one server at 127.0.0.1:9976), so every key built from it named a new server
/// each time. The game's connect address and the address the player dialed are stable and come first; the connection
/// string only when nothing else is known.</para>
/// </summary>
internal static class ConnectHostKey
{
    /// <summary>Shortest relay string treated as one (they are 27 characters).</summary>
    private const int RelayMinChars = 20;

    /// <summary>
    /// The stable key: <c>address:port</c> from the game's connect address for this connection, else from the address this
    /// process last dialed (Bootstrap's <c>LaunchGame</c> record), else the connection string (prefix stripped), else empty.
    /// An address that is itself a relay string is skipped, because it changes per connection. The game's own address
    /// comes first because Bootstrap's record is never cleared: a join that did not pass through <c>LaunchGame</c> would
    /// otherwise be keyed as the previous server.
    /// </summary>
    internal static string Choose(
        string? dialedAddress, int dialedPort,
        string? gameAddress, int gamePort,
        string? connectionString)
    {
        string game = Endpoint(gameAddress, gamePort);
        if (game.Length > 0) return game;
        string dialed = Endpoint(dialedAddress, dialedPort);
        if (dialed.Length > 0) return dialed;
        return ModManifest.NormalizeConnectHost(connectionString ?? "");
    }

    /// <summary><c>address:port</c> (address lowercased), or empty without a usable address or port.</summary>
    internal static string Endpoint(string? address, int port)
    {
        string a = (address ?? "").Trim();
        if (a.Length == 0 || port <= 0 || port > 65535 || IsRelayString(a)) return "";
        return a.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A key written before HOSTKEY1 from a relay string: base64 characters only, no <c>.</c> and no <c>:</c>, at least
    /// 20 characters, and not all hex (an EOS product user id, the whole key of an <c>EosP2P</c> join, is 32 hex). An
    /// address key always carries <c>:port</c>, so it never matches; a dotless LAN host name this long made only of
    /// base64 characters would be misread, and the cost is that its rows can be moved to a later key.
    /// </summary>
    internal static bool IsRelayString(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length < RelayMinChars) return false;
        bool hexOnly = true;
        foreach (char c in k)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '+' || c == '/' || c == '=' || c == '_' || c == '-';
            if (!ok) return false;
            if (!Uri.IsHexDigit(c)) hexOnly = false;
        }
        return !hexOnly;
    }

    /// <summary>A key that rows written under a relay string may be moved to: not empty and not a relay string.</summary>
    internal static bool IsStable(string? key)
    {
        string k = (key ?? "").Trim();
        return k.Length > 0 && !IsRelayString(k) && !string.Equals(k, ServerProfiles.UnknownHostKey, StringComparison.Ordinal);
    }
}
