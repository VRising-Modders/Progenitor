using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stages 6 and 7: the Core half of the dev bridge's <c>server_rules</c> op. Dev reaches this type by
/// reflection. Client actions use the page's own request path (<see cref="ServerRulesRows"/>), so a bridge write is what
/// the page sends and the server decides it like any other; the server side only reads (no direct set, so live tests take
/// the admin path). Nothing here ever returns a password.
/// </summary>
internal static class ServerRulesTestBridge
{
    // ---- client -----------------------------------------------------------------------------

    public static Dictionary<string, object?> Read()
    {
        var d = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "client",
            ["supported"] = ServerRulesRows.Supported(),
            ["canEdit"] = ServerRulesRows.CanEdit(),
            ["writesEnabled"] = ServerRulesRows.WritesEnabled,
#if !PROGENITOR_TESTS
            ["answers"] = ServerRulesClient.Answers,
            ["pending"] = ServerRulesClient.PendingCount,
#endif
        };
        d["state"] = ServerRulesRows.State is { } s ? Describe(s) : null;
        return d;
    }

    /// <summary>Ask the server for its state now (as the page does when it is drawn).</summary>
    public static Dictionary<string, object?> Get()
    {
        bool sent = ServerRulesRows.AskIfDue(0);
        var d = Read();
        d["result"] = sent ? "sent get" : "not sent";
        return d;
    }

    /// <summary>Send a new value as the page's Apply or switch does (the server decides it, with its audit line).</summary>
    public static Dictionary<string, object?> Write(string key, string value) => WriteWith(key, value, false);

    /// <summary>
    /// Review fix (scheduled wipes): a write the server says may reset the world needs <paramref name="confirm"/> (the bridge's
    /// <c>confirm: true</c>), as the page's dialog sends it; without it the server refuses and nothing is written.
    /// </summary>
    public static Dictionary<string, object?> WriteWith(string key, string value, bool confirm)
    {
        string result = "sent";
        ServerRulesRows.Send(key ?? "", value ?? "", (ok, message) => result = (ok ? "saved: " : "refused: ") + message, confirm);
        var d = Read();
        d["result"] = result;
        return d;
    }

    internal static Dictionary<string, object?> Describe(ServerRulesState s)
    {
        var rows = new List<object?>();
        foreach (var r in s.Rows)
        {
            bool password = string.Equals(r.Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase);
            rows.Add(new Dictionary<string, object?>
            {
                ["key"] = r.Key, ["id"] = ServerRules.Id(r.Key),
                ["loaded"] = password ? null : r.Loaded, ["next"] = password ? null : r.Next,
                ["pending"] = r.Pending, ["override"] = r.Override, ["editable"] = r.Editable, ["reason"] = r.Reason,
                ["lockedBy"] = r.LockedBy, ["source"] = r.Source,
            });
        }

        return new Dictionary<string, object?>
        {
            ["rows"] = rows,
            ["password"] = new Dictionary<string, object?> { ["set"] = s.PasswordSet, ["nextSet"] = s.PasswordNextSet },
            ["restartPending"] = s.RestartPending,
            ["merciless"] = new Dictionary<string, object?> { ["now"] = s.MercilessNow, ["next"] = s.MercilessNext },
            ["territory"] = s.Territory,
            ["daysRunning"] = s.DaysRunning,
            ["startDate"] = s.StartDate,
            ["standard"] = new Dictionary<string, object?> { ["now"] = s.StandardNow, ["mayChange"] = s.StandardMayChange },
            ["nextReset"] = s.NextReset,
            ["presetWarning"] = s.PresetWarning,
            ["adminReserve"] = s.AdminReserve,
            ["maxPlayersMax"] = s.MaxPlayersMax,
        };
    }

    // ---- server -----------------------------------------------------------------------------

    /// <summary>The server's files, layers, loaded values and overrides, and the state it answers with (server).</summary>
    public static Dictionary<string, object?> Server()
    {
#if !PROGENITOR_TESTS
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("server_rules server read runs on the server side");
#endif
        ServerRulesFacts facts;
        try { facts = ServerRulesServer.FactsProvider?.Invoke() ?? new ServerRulesFacts(); }
        catch (Exception e) { return new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.Message }; }
        facts.LockedBy ??= ServerSettingsLock.LockedBy;
        var state = ServerRulesStateBuilder.Build(facts);
        var overrides = new Dictionary<string, object?>();
        if (facts.Host != null)
            foreach (var spec in ServerRules.Specs)
                if (facts.Host.OverrideOf(spec.Key) is { } o) overrides[spec.Key] = o.Source;
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = "server",
            ["hostLayers"] = facts.Host?.Describe() ?? "",
            ["hostWriteTarget"] = facts.Host?.WriteTarget?.Path ?? "",
            ["saveFolder"] = facts.Host?.SaveFolder ?? "",
            ["gameLayers"] = facts.Game?.Describe() ?? "",
            ["gameWriteTarget"] = facts.Game?.WriteTarget?.Path ?? "",
            ["preset"] = facts.Game?.PresetName ?? "",
            ["presetMissing"] = facts.Game?.PresetMissing ?? false,
            ["difficultyPreset"] = facts.Game?.DifficultyName ?? "",
            ["overrides"] = overrides,
            ["tags"] = facts.Tags == null ? null : new List<string>(facts.Tags),
            ["state"] = Describe(state),
        };
    }
}
