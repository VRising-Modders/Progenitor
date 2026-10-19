using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// SERVEREMPTY1: the last server this client joined, for the Server tab's <b>Last server</b> row when it is not connected.
/// <c>BepInEx/config/progenitor-last-server.json</c>, written on each successful join of a server this client did not host
/// (a Progenitor server once the handshake said ok, a server without Progenitor once that was detected). Plain values:
/// the address, port and transport the game dialed, the server's name, and whether the join used a password — never the
/// password itself (the type has no field for it and the reader ignores one in the file).
/// </summary>
internal sealed class LastServerRecord
{
    public const int Schema = 1;

    public string Address { get; set; } = "";
    public int Port { get; set; }

    /// <summary><c>SteamIPv4</c> or <c>Lidgren</c> (<see cref="JoinTarget.CanonicalProtocol"/>).</summary>
    public string Protocol { get; set; } = "";

    /// <summary>The server's name when one was known at the join (its signed catalog's, or this PC's folder's); may be empty.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>
    /// The server wanted a password at the last outcome (whether — never the password): true after a join with one or a
    /// password refusal, false after a join without one. A hint for the readback only: Join always connects without a
    /// password first and lets the game ask only when the server refuses (JOINPW1).
    /// </summary>
    public bool NeedsPassword { get; set; }

    /// <summary>When the join was recorded (ISO UTC).</summary>
    public string Joined { get; set; } = "";

    /// <summary><c>1.2.3.4:9876</c>.</summary>
    public string Endpoint => Address.Trim() + ":" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The name the row shows: the server's name, else its address.</summary>
    public string DisplayName => JoinText.NameOrAddress(ServerName, Endpoint);

    public JoinTarget ToTarget() => new()
    {
        Address = Address,
        Port = Port,
        Protocol = Protocol,
        ServerName = ServerName,
        // JOINPW1 (live 2026-10-06): a stale needsPassword opened the game's prompt for a server that no longer had a
        // password. The hint never decides: connect without one, and on an IncorrectPassword refusal the game's own prompt
        // asks for it (RetainedPassword can fill it). Nothing is stored here.
        NeedsPassword = false,
        PromptForPassword = false,
        PromptOnPasswordRefusal = true,
    };

    /// <summary>This record with <see cref="NeedsPassword"/> set to <paramref name="needsPassword"/> (the join time kept).</summary>
    public LastServerRecord WithPassword(bool needsPassword) => new()
    {
        Address = Address,
        Port = Port,
        Protocol = Protocol,
        ServerName = ServerName,
        NeedsPassword = needsPassword,
        Joined = Joined,
    };

    /// <summary>The record names <paramref name="target"/>'s address, port and transport.</summary>
    public bool Names(JoinTarget? target) =>
        target != null
        && string.Equals(Address.Trim(), target.Address.Trim(), StringComparison.OrdinalIgnoreCase)
        && Port == target.Port
        && string.Equals(JoinTarget.CanonicalProtocol(Protocol), JoinTarget.CanonicalProtocol(target.Protocol), StringComparison.Ordinal);

    /// <summary>The record of a join to <paramref name="target"/>, or null when that join is not one to remember.</summary>
    public static LastServerRecord? From(JoinTarget? target, DateTime nowUtc)
    {
        if (!LastServerStore.Remembers(target)) return null;
        return new LastServerRecord
        {
            Address = target!.Address.Trim(),
            Port = target.Port,
            Protocol = JoinTarget.CanonicalProtocol(target.Protocol),
            ServerName = (target.ServerName ?? "").Trim(),
            NeedsPassword = target.NeedsPassword,
            Joined = ModManifestJson.FormatIssued(nowUtc),
        };
    }

    /// <summary>The same server, name and password use (the join time aside): nothing new to write.</summary>
    public bool SameAs(LastServerRecord? other) =>
        other != null
        && string.Equals(Address.Trim(), other.Address.Trim(), StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && string.Equals(Protocol, other.Protocol, StringComparison.Ordinal)
        && string.Equals(ServerName, other.ServerName, StringComparison.Ordinal)
        && NeedsPassword == other.NeedsPassword;

    /// <summary>The JSON on disk: sorted keys, plain values. There is no password key.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("address", Address);
            w.WriteString("joined", Joined);
            w.WriteBoolean("needsPassword", NeedsPassword);
            w.WriteNumber("port", Port);
            w.WriteString("protocol", Protocol);
            w.WriteNumber("schema", Schema);
            w.WriteString("serverName", ServerName);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parse; false (and null) for anything that is not a usable record of this schema (no address, port or known
    /// transport). Unknown keys, a <c>password</c> among them, are ignored.
    /// </summary>
    public static bool TryParse(string? json, out LastServerRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json) || json!.Length > LastServerStore.MaxBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int s) || s != Schema) return false;
            var r = new LastServerRecord
            {
                Address = Str(root, "address"),
                Port = root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out int p) ? p : 0,
                Protocol = Str(root, "protocol"),
                ServerName = Str(root, "serverName"),
                NeedsPassword = root.TryGetProperty("needsPassword", out var pw) && pw.ValueKind == JsonValueKind.True,
                Joined = Str(root, "joined"),
            };
            // Only a transport the game can dial again; an empty one would read as SteamIPv4, so it is refused here.
            if (r.Protocol.Trim().Length == 0 || !r.ToTarget().CanReconnect) return false;
            r.Protocol = JoinTarget.CanonicalProtocol(r.Protocol);
            record = r;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}

/// <summary>SERVEREMPTY1: the last-server file's rules, game-free. Written atomically; a bad file reads as none.</summary>
internal static class LastServerStore
{
    public const string FileName = "progenitor-last-server.json";
    public const int MaxBytes = 8 * 1024;

    /// <summary>
    /// A join worth remembering: an address, port and transport the game can dial again, to a server this client does not
    /// host (a private game it hosts is started from the game's own menu, never rejoined from here).
    /// </summary>
    public static bool Remembers(JoinTarget? target) =>
        target != null && !target.HostedByThisClient && target.CanReconnect && (target.Protocol ?? "").Trim().Length > 0
        && JoinTarget.CanonicalProtocol(target.Protocol).Length > 0;

    public static void Write(string path, LastServerRecord record)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));
        if (record == null) throw new ArgumentNullException(nameof(record));
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, record.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); }
            finally
            {
                try { File.Delete(tmp); }
                catch { /* a leftover tmp is never read */ }
            }
        }
    }

    /// <summary>The record on disk, or null (missing, too large, malformed, or not dialable). Never throws.</summary>
    public static LastServerRecord? Read(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaxBytes) return null;
            return LastServerRecord.TryParse(File.ReadAllText(path), out var r) ? r : null;
        }
        catch { return null; }
    }
}

/// <summary>What the Server tab's <b>Join</b> on the Last server row does now.</summary>
internal enum LastServerJoinState
{
    /// <summary>No row: no last server, a private game this client hosts, or the tab is not in its not-connected state.</summary>
    Hidden = 0,

    /// <summary>The row and an enabled <b>Join</b>.</summary>
    Ready = 1,

    /// <summary>The row with <b>Join</b> grayed: a join is already in progress.</summary>
    Busy = 2,
}

/// <summary>SERVEREMPTY1: the facts the Last server row is drawn from (live: the file, Bootstrap and the join flow).</summary>
internal sealed class LastServerFacts
{
    public LastServerRecord? Record { get; init; }

    /// <summary>Bootstrap's <c>IsHostingPrivateGame</c>.</summary>
    public bool HostingPrivateGame { get; init; }

    /// <summary>A join is under way: Join now's reconnect, or a client world on its way in.</summary>
    public bool JoinInProgress { get; init; }

    /// <summary>Whether the row is drawn, and whether its Join can be pressed.</summary>
    public LastServerJoinState JoinState =>
        Record == null || HostingPrivateGame ? LastServerJoinState.Hidden
        : JoinInProgress ? LastServerJoinState.Busy
        : LastServerJoinState.Ready;

    public static string StateName(LastServerJoinState state) => state switch
    {
        LastServerJoinState.Ready => "ready",
        LastServerJoinState.Busy => "busy",
        _ => "hidden",
    };
}
