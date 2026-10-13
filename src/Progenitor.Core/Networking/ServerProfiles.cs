using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Networking;

/// <summary>One server this client has joined, as the per-server mod pause remembers it.</summary>
internal sealed class ServerProfile
{
    /// <summary>
    /// The connect address, as <c>ModManifest.ReadLiveConnectHost</c> reads it (HOSTKEY1: the dialed <c>address:port</c>,
    /// lowercased). The key.
    /// </summary>
    internal string Host { get; set; } = "";

    /// <summary>
    /// Pause this client's hot-loaded mods automatically when this server does not run them.
    /// Default true. Set <c>"autoPause": false</c> in the file to opt a server out.
    /// </summary>
    internal bool AutoPause { get; set; } = true;

    /// <summary>The plugin GUIDs the last pause on this server took. Display only.</summary>
    internal List<string> LastPaused { get; set; } = new();

    /// <summary>ISO-8601 UTC of the last pause or detection on this server. Display only.</summary>
    internal string LastSeenUtc { get; set; } = "";

    /// <summary>Server name from its signed catalog, when one arrived. Display only.</summary>
    internal string ServerName { get; set; } = "";

    /// <summary>Operator key fingerprint from its signed catalog, when one arrived. Display only.</summary>
    internal string OperatorFingerprint { get; set; } = "";

    /// <summary>
    /// null: unknown; false: the last join saw no Progenitor handshake request. Display only: the
    /// detection runs again on every join.
    /// </summary>
    internal bool? RunsProgenitor { get; set; }

    /// <summary>
    /// CLIENTPOLICY2: the client mod policy this server last signed (<see cref="ClientPolicyView.Remembered"/>), so the
    /// next join's automatic pause knows it before the report (the request carries only a capability bit). Empty: none.
    /// </summary>
    internal string ClientPolicy { get; set; } = "";

    /// <summary>ISO-8601 UTC of when <see cref="ClientPolicy"/> was recorded (the oldest policies go past the cap).</summary>
    internal string ClientPolicySeenUtc { get; set; } = "";
}

/// <summary>
/// <c>BepInEx/config/progenitor-server-profiles.json</c>: per-server settings and memory for the
/// per-server mod pause, client side. Same folder as <c>progenitor-trusted-operators.json</c>
/// (<see cref="ModManifest.ConfigDirectory"/>). Not ModSave: it belongs to this client, not to a
/// world. A missing or unreadable file means every server uses the defaults (auto-pause on), and a
/// write failure is logged and never costs a join.
/// </summary>
internal static class ServerProfiles
{
    internal const string FileName = "progenitor-server-profiles.json";
    internal const int Schema = 1;
    internal const int MaxServers = 256;

    /// <summary>The key used when the connect address could not be read.</summary>
    internal const string UnknownHostKey = "(unknown)";

    private static readonly object Gate = new();

    /// <summary>Tests only: a directory instead of <c>BepInEx/config</c>.</summary>
#pragma warning disable CS0649 // assigned by the test project only
    internal static string? TestDirectory;
#pragma warning restore CS0649

    internal static string FilePath =>
        Path.Combine(TestDirectory ?? ModManifest.ConfigDirectory, FileName);

    internal static string KeyOf(string? host)
    {
        string h = (host ?? "").Trim();
        return h.Length == 0 ? UnknownHostKey : h.ToLowerInvariant();
    }

    /// <summary>The profile for <paramref name="host"/>, or a default one (not written).</summary>
    private static bool _warnedUnreadable;

    internal static ServerProfile Get(string? host)
    {
        lock (Gate)
        {
            TryLoad(out var all);
            return all.TryGetValue(KeyOf(host), out var p) ? p : new ServerProfile { Host = KeyOf(host) };
        }
    }

    /// <summary>
    /// Auto-pause for this server: true unless the profile opts out. Fail-safe: false when the file
    /// exists but cannot be read or parsed, because an opt-out in it cannot be ruled out.
    /// </summary>
    internal static bool AutoPause(string? host)
    {
        try
        {
            lock (Gate)
            {
                if (!TryLoad(out var all)) return false;
                return !all.TryGetValue(KeyOf(host), out var p) || p.AutoPause;
            }
        }
        catch { return false; }
    }

    /// <summary>Opt a server in or out (writes the file).</summary>
    internal static void SetAutoPause(string? host, bool enabled) =>
        Update(host, p => p.AutoPause = enabled);

    internal static void NotePaused(string? host, IReadOnlyList<string> ids, DateTime nowUtc) =>
        Update(host, p =>
        {
            p.LastPaused = new List<string>(ids ?? Array.Empty<string>());
            p.LastSeenUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        });

    internal static void NoteProgenitor(string? host, bool runsProgenitor, DateTime nowUtc) =>
        Update(host, p =>
        {
            p.RunsProgenitor = runsProgenitor;
            p.LastSeenUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        });

    /// <summary>Record the server name and fingerprint, only when a profile for this host already exists.</summary>
    internal static void NoteIdentityIfKnown(string? host, string? serverName, string? fingerprint) =>
        Update(host, p =>
        {
            if (!string.IsNullOrEmpty(serverName)) p.ServerName = serverName!;
            if (!string.IsNullOrEmpty(fingerprint)) p.OperatorFingerprint = fingerprint!;
            p.RunsProgenitor = true;
        }, createIfMissing: false);

    /// <summary>
    /// HOSTKEY1: a verified catalog from <paramref name="host"/> signed by <paramref name="fingerprint"/> claims the
    /// profiles written under per-connection relay strings (<see cref="ConnectHostKey.IsRelayString"/>) for that operator.
    /// They merge into the host's profile (created when missing): auto-pause stays off if any of them or the host's
    /// profile opted out; the server name, paused list and remembered policy fill only what the host's profile lacks,
    /// from the newest that has one. They are dropped. A relay profile without a fingerprint can never be matched again
    /// and is dropped too. True when the file changed.
    /// </summary>
    internal static bool AdoptRelayProfiles(string? host, string? fingerprint)
    {
        string key = KeyOf(host);
        string fp = (fingerprint ?? "").Trim();
        if (!ConnectHostKey.IsStable(key) || fp.Length == 0) return false;
        lock (Gate)
        {
            if (!TryLoad(out var all)) return false;
            var claimed = new List<ServerProfile>();
            var orphans = new List<string>();
            foreach (var pair in all)
            {
                if (!ConnectHostKey.IsRelayString(pair.Key)) continue;
                if (string.Equals(pair.Value.OperatorFingerprint, fp, StringComparison.OrdinalIgnoreCase)) claimed.Add(pair.Value);
                else if (pair.Value.OperatorFingerprint.Length == 0) orphans.Add(pair.Key);
            }
            if (claimed.Count == 0) return false;

            foreach (var p in claimed) all.Remove(KeyOf(p.Host));
            foreach (string k in orphans) all.Remove(k);
            if (!all.TryGetValue(key, out var target))
            {
                target = new ServerProfile { Host = key };
                all[key] = target;
            }

            // Newest first, so each field the host's profile lacks is filled from the newest relay profile that has it.
            claimed.Sort((a, b) => string.CompareOrdinal(b.LastSeenUtc, a.LastSeenUtc));
            foreach (var p in claimed)
            {
                target.AutoPause &= p.AutoPause;
                if (target.ServerName.Length == 0) target.ServerName = p.ServerName;
                if (target.OperatorFingerprint.Length == 0) target.OperatorFingerprint = p.OperatorFingerprint;
                if (target.LastPaused.Count == 0 && p.LastPaused.Count > 0) target.LastPaused = new List<string>(p.LastPaused);
                if (target.LastSeenUtc.Length == 0) target.LastSeenUtc = p.LastSeenUtc;
                target.RunsProgenitor ??= p.RunsProgenitor;
            }

            if (target.ClientPolicy.Length == 0)
            {
                ServerProfile? policy = null;
                foreach (var p in claimed)
                    if (p.ClientPolicy.Length > 0 && (policy == null || string.CompareOrdinal(p.ClientPolicySeenUtc, policy.ClientPolicySeenUtc) > 0))
                        policy = p;
                if (policy != null)
                {
                    target.ClientPolicy = policy.ClientPolicy;
                    target.ClientPolicySeenUtc = policy.ClientPolicySeenUtc;
                }
            }
            Save(all);
            return true;
        }
    }

    /// <summary>CLIENTPOLICY2: servers whose policy is remembered at most (the oldest are forgotten first).</summary>
    internal const int MaxPolicies = 32;

    /// <summary>
    /// CLIENTPOLICY2: remember the client mod policy a server signed (empty text forgets it). Written only when it
    /// changed; past <see cref="MaxPolicies"/> servers the oldest remembered policy is dropped (its profile stays).
    /// </summary>
    internal static void NotePolicy(string? host, string policy, DateTime nowUtc)
    {
        string text = policy ?? "";
        lock (Gate)
        {
            if (!TryLoad(out var all)) return;
            string key = KeyOf(host);
            if (!all.TryGetValue(key, out var p))
            {
                if (text.Length == 0 || all.Count >= MaxServers) return;
                p = new ServerProfile { Host = key };
                all[key] = p;
            }

            if (string.Equals(p.ClientPolicy, text, StringComparison.Ordinal)) return;
            p.ClientPolicy = text;
            p.ClientPolicySeenUtc = text.Length == 0 ? "" : nowUtc.ToString("o", CultureInfo.InvariantCulture);
            var withPolicy = new List<ServerProfile>();
            foreach (var x in all.Values) if (x.ClientPolicy.Length > 0) withPolicy.Add(x);
            if (withPolicy.Count > MaxPolicies)
            {
                withPolicy.Sort((a, b) => string.CompareOrdinal(a.ClientPolicySeenUtc, b.ClientPolicySeenUtc));
                for (int i = 0; i < withPolicy.Count - MaxPolicies; i++)
                {
                    withPolicy[i].ClientPolicy = "";
                    withPolicy[i].ClientPolicySeenUtc = "";
                }
            }

            Save(all);
        }
    }

    /// <summary>CLIENTPOLICY2: the policy remembered for <paramref name="host"/>, or empty.</summary>
    internal static string PolicyOf(string? host)
    {
        try
        {
            lock (Gate)
            {
                if (!TryLoad(out var all)) return "";
                return all.TryGetValue(KeyOf(host), out var p) ? p.ClientPolicy : "";
            }
        }
        catch { return ""; }
    }

    /// <summary>
    /// Read-modify-write. A file that exists but cannot be read is never overwritten: that would
    /// erase every other server's entry, opt-outs included.
    /// </summary>
    private static void Update(string? host, Action<ServerProfile> change, bool createIfMissing = true)
    {
        lock (Gate)
        {
            if (!TryLoad(out var all)) return;
            string key = KeyOf(host);
            if (!all.TryGetValue(key, out var p))
            {
                if (!createIfMissing) return;
                if (all.Count >= MaxServers) return;
                p = new ServerProfile { Host = key };
                all[key] = p;
            }
            change(p);
            Save(all);
        }
    }

    /// <summary>False when the file exists and could not be read or parsed (logged once per process).</summary>
    private static bool TryLoad(out Dictionary<string, ServerProfile> result)
    {
        result = new Dictionary<string, ServerProfile>(StringComparer.OrdinalIgnoreCase);
        string path = FilePath;
        if (!File.Exists(path)) return true;
        string error;
        try
        {
            if (TryParse(File.ReadAllText(path), out var list, out error))
            {
                foreach (var p in list) result[KeyOf(p.Host)] = p;
                return true;
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        if (!_warnedUnreadable)
        {
            _warnedUnreadable = true;
            Plugin.Logger?.LogWarning("ServerModPause: " + FileName + " could not be read (" + error
                + "); automatic pausing is off and the file is left untouched until it can be read.");
        }
        result.Clear();
        return false;
    }

    /// <summary>Atomic: a temp file beside it, then <see cref="File.Replace(string, string, string?)"/> (or a move for the first write).</summary>
    private static void Save(Dictionary<string, ServerProfile> all)
    {
        string path = FilePath;
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, Serialize(all.Values));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerModPause: writing " + FileName + " threw: " + e.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests() => _warnedUnreadable = false;
#endif

    /// <summary>
    /// Written with <see cref="Utf8JsonWriter"/> rather than <c>JsonNode</c>: node values created
    /// from CLR strings serialize through the reflection resolver, which a trimmed or
    /// reflection-disabled host refuses.
    /// </summary>
    internal static string Serialize(IEnumerable<ServerProfile> profiles)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("servers");
            foreach (var p in profiles)
            {
                if (p == null) continue;
                w.WriteStartObject();
                w.WriteString("host", p.Host);
                w.WriteBoolean("autoPause", p.AutoPause);
                w.WriteStartArray("lastPaused");
                foreach (var id in p.LastPaused) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteString("lastSeenUtc", p.LastSeenUtc);
                w.WriteString("serverName", p.ServerName);
                w.WriteString("operatorFingerprint", p.OperatorFingerprint);
                if (p.RunsProgenitor.HasValue) w.WriteBoolean("runsProgenitor", p.RunsProgenitor.Value);
                // CLIENTPOLICY2: written only when remembered, so a file without one is byte-identical to before.
                if (p.ClientPolicy.Length > 0)
                {
                    w.WriteString("clientPolicy", p.ClientPolicy);
                    w.WriteString("clientPolicySeenUtc", p.ClientPolicySeenUtc);
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool TryParse(string? json, out List<ServerProfile> profiles, out string error)
    {
        profiles = new List<ServerProfile>();
        error = "";
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject root) { error = "not a JSON object"; return false; }
            if (root["servers"] is not JsonArray servers) return true;
            foreach (var node in servers)
            {
                if (node is not JsonObject o) continue;
                string host = Str(o, "host");
                if (host.Length == 0) continue;
                var p = new ServerProfile
                {
                    Host = host,
                    AutoPause = Bool(o, "autoPause") ?? true,
                    LastSeenUtc = Str(o, "lastSeenUtc"),
                    ServerName = Str(o, "serverName"),
                    OperatorFingerprint = Str(o, "operatorFingerprint"),
                    RunsProgenitor = Bool(o, "runsProgenitor"),
                    ClientPolicy = Str(o, "clientPolicy"),
                    ClientPolicySeenUtc = Str(o, "clientPolicySeenUtc"),
                };
                if (o["lastPaused"] is JsonArray ids)
                    foreach (var id in ids)
                        if (id is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) p.LastPaused.Add(s);
                profiles.Add(p);
                if (profiles.Count >= MaxServers) break;
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            profiles.Clear();
            return false;
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
