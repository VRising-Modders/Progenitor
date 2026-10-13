using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Splits a Direct-Connect address into an IPv4 host and port <em>before</em>
/// <c>ConnectAddress.CreateSteamIPv4</c>. That factory takes the string as a
/// <c>FixedString32Bytes</c> host and does not parse; passing <c>127.0.0.1:9976</c>
/// or <c>SteamIPv4://…</c> through would land the colon in the host field. Kept
/// free of game types so tests can cover the forms without Stunlock.Network.
/// </summary>
public static class ConnectAddressParser
{
    /// <summary>Staged test-server game port (query is 9977; that is not the connect port).</summary>
    public const int DefaultPort = 9976;

    /// <summary>IPv4 host + TCP port ready for <c>CreateSteamIPv4</c>.</summary>
    public readonly struct Parsed
    {
        public readonly string Host;
        public readonly int Port;

        public Parsed(string host, int port)
        {
            Host = host;
            Port = port;
        }
    }

    /// <summary>
    /// Accepts <c>host</c>, <c>host:port</c>, and <c>SteamIPv4://host:port</c>. Host must be
    /// IPv4 (<see cref="IPAddress.TryParse"/> + <see cref="AddressFamily.InterNetwork"/>);
    /// hostnames and IPv6 are refused. When <paramref name="explicitPort"/> is set it wins
    /// over a port split from the address; otherwise the split port, otherwise
    /// <paramref name="defaultPort"/>.
    /// </summary>
    public static Parsed Parse(string? address, int? explicitPort = null, int defaultPort = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("address is required");

        var raw = address.Trim();

        int schemeSep = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep >= 0)
        {
            var scheme = raw.Substring(0, schemeSep);
            if (!scheme.Equals("SteamIPv4", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"unsupported address scheme '{scheme}' (only SteamIPv4://, IPv4 host, or host:port are accepted)");
            }
            raw = raw.Substring(schemeSep + 3).Trim();
        }

        if (raw.Length == 0)
            throw new ArgumentException("address host is empty");

        if (raw.StartsWith("[", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"IPv6 addresses are not supported (got '{address}'); pass an IPv4 address");
        }

        string hostPart;
        int? addressPort = null;
        int colon = raw.LastIndexOf(':');
        if (colon >= 0)
        {
            if (raw.IndexOf(':') != colon)
            {
                throw new ArgumentException(
                    $"address has more than one ':' (IPv6 is not supported): '{address}'");
            }

            hostPart = raw.Substring(0, colon).Trim();
            var portPart = raw.Substring(colon + 1).Trim();
            if (portPart.Length == 0)
                throw new ArgumentException($"address port is empty in '{address}'");
            if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
                throw new ArgumentException($"address port is not an integer in '{address}'");
            addressPort = parsedPort;
        }
        else
        {
            hostPart = raw;
        }

        if (string.IsNullOrWhiteSpace(hostPart))
            throw new ArgumentException("address host is empty");

        if (!IPAddress.TryParse(hostPart, out var ip))
        {
            throw new ArgumentException(
                $"address host is not an IP address (hostnames are not supported): '{hostPart}'");
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException(
                $"IPv6 addresses are not supported (got '{hostPart}'); pass an IPv4 address");
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                $"address host must be IPv4 (got '{hostPart}', {ip.AddressFamily})");
        }

        int port = explicitPort ?? addressPort ?? defaultPort;
        if (port <= 0 || port > 65535)
            throw new ArgumentException($"port must be 1–65535 (got {port})");

        return new Parsed(ip.ToString(), port);
    }
}
