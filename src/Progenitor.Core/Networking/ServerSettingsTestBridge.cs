#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// TEXTLIMIT1: the Core half of the dev bridge's <c>setting</c> op (both sides). Dev reaches this type by
/// reflection (Core grants Dev no <c>InternalsVisibleTo</c>); it is not a plugin API and no network path
/// reaches it. Main thread.
///
/// <para><c>Get</c> reads a synced setting's stored value on this side (the server's own value, or the
/// client's synced copy); <c>Audit</c> tails the server's <c>progenitor-settings.log</c>; <c>Request</c>
/// sends an administrator edit through <see cref="ServerSettings"/> exactly as the page's Apply does,
/// optionally without this side's length pre-check (a client that skipped it), and <c>Result</c> reads
/// its answer. The server decides every request the same way whoever sent it.</para>
/// </summary>
internal static class ServerSettingsTestBridge
{
    private const int MaxTickets = 16;
    private static readonly Dictionary<int, Ticket> Tickets = new();
    private static int _nextTicket;

    /// <summary>The stored value of <paramref name="rowId"/> (<c>plugin/Section/Key</c>) on this side, with its limits.</summary>
    public static Dictionary<string, object?> Get(string rowId)
    {
        if (!TrySplit(rowId, out string plugin, out string section, out string key))
            return Error("args.row must be <plugin guid>/<Section>/<Key>");
        if (!ConfigSync.TryFindEntry(plugin, section, key, out var entry) || entry == null)
            return Error("no synced setting is registered as " + rowId + " on this side");

        string value;
        try { value = SettingsEditors.Format(entry.BoxedValue); }
        catch (Exception e) { return Error("the value could not be read: " + e.Message); }
        var (max, multiline) = ServerSettings.TextLimitFor(rowId);
        var d = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = Plugin.IsServerProcess ? "server" : "client",
            ["row"] = rowId,
            ["type"] = entry.SettingType?.FullName ?? "",
            ["synced"] = true,
            ["locked"] = SafeLocked(plugin, section, key),
            ["adminEditable"] = ServerSettings.IsEditable(rowId),
            ["canRequest"] = !Plugin.IsServerProcess && ServerSettings.CanRequest(rowId),
        };
        Describe(d, value);
        if (entry.SettingType == typeof(string))
        {
            d["declared"] = new Dictionary<string, object?>
            {
                ["maxLength"] = max,
                ["multiline"] = multiline,
                ["limit"] = ServerSettingsLimits.Declared(max, multiline),
                ["effective"] = ServerSettingsLimits.Effective(max, multiline),
            };
            d["fits"] = ServerSettingsLimits.Check(value, max, multiline) ?? "yes";
        }
        return d;
    }

    /// <summary>The last <paramref name="lines"/> lines of <c>BepInEx/progenitor-settings.log</c> (server).</summary>
    public static Dictionary<string, object?> Audit(int lines)
    {
        if (!Plugin.IsServerProcess) return Error("the settings audit log is written on the server; run this on the server side");
        string root = "";
        try { root = BepInEx.Paths.BepInExRootPath ?? ""; } catch { /* */ }
        string path = Progenitor.Install.SettingsAuditLog.PathFor(root);
        var tail = new List<string>();
        int n = Math.Max(1, Math.Min(200, lines));
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    tail.Add(line);
                    if (tail.Count > n) tail.RemoveAt(0);
                }
            }
        }
        catch (Exception e) { return Error("the audit log could not be read: " + e.Message); }
        return new Dictionary<string, object?> { ["ok"] = true, ["path"] = path, ["lines"] = tail };
    }

    /// <summary>
    /// Send an administrator edit of <paramref name="rowId"/> as the page's Apply does. The expected
    /// (conflict) value is this side's displayed value unless <paramref name="expected"/> is given.
    /// Returns a ticket for <see cref="Result"/>; a refusal decided here completes it at once.
    /// </summary>
    public static Dictionary<string, object?> Request(string rowId, string value, bool skipClientCheck, string? expected)
    {
        if (Plugin.IsServerProcess) return Error("requests are sent from a client");
        if (Tickets.Count >= MaxTickets)
        {
            foreach (var old in Tickets.Values)
            {
                try { old.Lease?.Dispose(); } catch { /* */ }
            }
            Tickets.Clear();
        }
        int id = ++_nextTicket;
        var ticket = new Ticket();
        Tickets[id] = ticket;
        try
        {
            ticket.Lease = ServerSettings.RequestCore(rowId ?? "", value ?? "", expected, result =>
            {
                ticket.Done = true;
                ticket.Result = result;
            }, clientCheck: !skipClientCheck);
        }
        catch (Exception e)
        {
            ticket.Done = true;
            ticket.Result = new ServerSettingResult(false, "", e.Message);
        }

        var d = new Dictionary<string, object?> { ["ok"] = true, ["ticket"] = id, ["clientCheck"] = !skipClientCheck };
        Describe(d, value ?? "", "sent");
        return d;
    }

    /// <summary>A request's answer: <c>{done, success, value, error}</c> (the value as the server stored it).</summary>
    public static Dictionary<string, object?> Result(int ticket)
    {
        if (!Tickets.TryGetValue(ticket, out var t)) return Error("unknown ticket " + ticket);
        var d = new Dictionary<string, object?> { ["ok"] = true, ["ticket"] = ticket, ["done"] = t.Done };
        if (!t.Done) return d;
        d["success"] = t.Result.Success;
        d["error"] = t.Result.Error ?? "";
        Describe(d, t.Result.Value ?? "", "answered");
        Tickets.Remove(ticket);
        try { t.Lease?.Dispose(); } catch { /* */ }
        return d;
    }

    /// <summary>Forget an unanswered ticket (the bridge's timeout path).</summary>
    public static void Forget(int ticket)
    {
        if (!Tickets.TryGetValue(ticket, out var t)) return;
        Tickets.Remove(ticket);
        try { t.Lease?.Dispose(); } catch { /* */ }
    }

    /// <summary>A value as the readback shows it: lengths, line count and hash, and the text itself (or its ends).</summary>
    internal static void Describe(Dictionary<string, object?> d, string value, string prefix = "")
    {
        string p(string name) => prefix.Length == 0 ? name : prefix + char.ToUpperInvariant(name[0]) + name.Substring(1);
        value ??= "";
        d[p("length")] = value.Length;
        d[p("utf8Bytes")] = ServerSettingsLimits.Utf8Bytes(value);
        d[p("escapedBytes")] = ServerSettingsLimits.EscapedBytes(value);
        d[p("lineBreaks")] = Count(value, '\n');
        d[p("hash")] = UiTextTestInput.Hash(value);
        if (value.Length <= 200) d[p("value")] = value;
        else
        {
            d[p("head")] = value.Substring(0, 60);
            d[p("tail")] = value.Substring(value.Length - 60);
        }
    }

    private static int Count(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }

    private static bool SafeLocked(string plugin, string section, string key)
    {
        try { return ConfigSync.IsLocked(plugin, section, key); }
        catch { return false; }
    }

    private static bool TrySplit(string? rowId, out string plugin, out string section, out string key)
    {
        plugin = section = key = "";
        if (string.IsNullOrWhiteSpace(rowId)) return false;
        var parts = rowId!.Split('/');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0) return false;
        plugin = parts[0];
        section = parts[1];
        key = parts[2];
        return true;
    }

    private static Dictionary<string, object?> Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private sealed class Ticket
    {
        internal bool Done;
        internal ServerSettingResult Result;
        internal IDisposable? Lease;
    }
}
#endif
