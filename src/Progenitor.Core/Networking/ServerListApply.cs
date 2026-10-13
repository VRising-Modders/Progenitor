using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>
/// SERVERDESC1: what the apply loop needs from the game, so the loop itself is game-free and tested with a
/// fake. The live implementation (<c>ServerListGame</c>) reads and writes
/// <c>SettingsManager.ServerHostSettings.Description</c>, calls
/// <c>PlatformSystemBase.GetSystem().SetServerDescription</c> and <c>SteamGameServer.SetKeyValue</c>, and
/// resolves the settings files with the game's own paths. Main thread only.
/// </summary>
internal interface IServerListGame
{
    /// <summary>The game's settings object exists (the description can be read and set).</summary>
    bool SettingsReady { get; }

    /// <summary>The live <c>Description</c>, or null when it cannot be read.</summary>
    string? LiveDescription { get; }

    /// <summary>Set the live <c>Description</c>; the game's own one-second loop sends it to EOS.</summary>
    void SetLiveDescription(string text);

    /// <summary>The live listing flags, or null when unknown.</summary>
    (bool Steam, bool Eos)? Listing { get; }

    /// <summary>The Steam game server is signed in (<c>PlatformSystemBase._ServerInitSuccess</c>).</summary>
    bool SteamReady { get; }

    /// <summary><c>PlatformSystemBase.GetSystem().SetServerDescription(text)</c>: <c>desc0</c>, <c>desc1</c>, … of 120 characters.</summary>
    void SteamPublish(string text);

    /// <summary><c>SteamGameServer.SetKeyValue(key, value)</c>.</summary>
    void SteamSetKey(string key, string value);

    /// <summary>The settings layers with the game's own paths.</summary>
    ServerHostSettingsResolution Resolve();
}

/// <summary>The row values the loop composes from (the server's own, or the bridge's).</summary>
internal sealed class ServerListValues
{
    internal ServerListValues(string raw, bool signature, ResetSchedule? reset, TimeZoneInfo? zone)
    {
        Raw = raw ?? "";
        Signature = signature;
        Reset = reset;
        Zone = zone ?? TimeZoneInfo.Utc;
    }

    public string Raw { get; }
    public bool Signature { get; }
    public ResetSchedule? Reset { get; }
    public TimeZoneInfo Zone { get; }
}

/// <summary>What one apply did, for the log, the page's status line and the bridge.</summary>
internal sealed class ServerListReport
{
    public DateTime AtUtc { get; set; }
    public string Reason { get; set; } = "";
    public string Raw { get; set; } = "";
    public bool Signature { get; set; }
    public string Rendered { get; set; } = "";
    public string Composed { get; set; } = "";
    public bool Cut { get; set; }
    public int Room { get; set; }
    public string LiveBefore { get; set; } = "";
    public string LiveAfter { get; set; } = "";
    public string LiveError { get; set; } = "";

    /// <summary><c>published</c>, <c>waiting</c> (not signed in yet; retried each second) or <c>failed</c>.</summary>
    public string Steam { get; set; } = "";
    public string SteamError { get; set; } = "";

    /// <summary>The <c>desc{i}</c> keys the last Steam publish set to text.</summary>
    public int SteamChunks { get; set; }

    /// <summary>The higher <c>desc{i}</c> keys set to <c>""</c> because the new text no longer uses them.</summary>
    public List<string> SteamCleared { get; set; } = new();

    public string PersistPath { get; set; } = "";
    public string PersistLayer { get; set; } = "";
    public string Persist { get; set; } = "";
    public string Override { get; set; } = "";
    public string OverrideSource { get; set; } = "";
    public string Layers { get; set; } = "";
    public bool? ListOnSteam { get; set; }
    public bool? ListOnEos { get; set; }

    /// <summary>The one line the page shows under the rows (synced to admins).</summary>
    public string Status(TimeZoneInfo? zone)
    {
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Utc);
        var parts = new List<string>(5)
        {
            "Published " + at.ToString("MMM d, h:mm tt", CultureInfo.GetCultureInfo("en-US")) + " (server time)",
        };
        if (LiveError.Length > 0) parts.Add("not applied: " + LiveError);
        parts.Add(Steam switch
        {
            "published" => "Steam updated",
            "waiting" => "Steam updates once the server is signed in",
            _ => "Steam not updated: " + SteamError,
        });
        parts.Add(Persist.StartsWith("failed", StringComparison.Ordinal) || Persist.StartsWith("notarget", StringComparison.Ordinal)
            ? "not saved: " + Persist
            : "saved in " + (PersistLayer == "save" ? "the save folder's " : "") + "ServerHostSettings.json");
        if (ListOnSteam == false && ListOnEos == false) parts.Add("this server is not listed (ListOnSteam and ListOnEOS are off)");
        string line = string.Join(" · ", parts) + ".";
        return Override.Length > 0 ? line + " " + Override : line;
    }

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["at"] = ResetCountdown.FormatUtc(AtUtc),
        ["reason"] = Reason,
        ["raw"] = Raw,
        ["signature"] = Signature,
        ["rendered"] = Rendered,
        ["composed"] = Composed,
        ["composedLength"] = Composed.Length,
        ["cut"] = Cut,
        ["room"] = Room,
        ["liveBefore"] = LiveBefore,
        ["liveAfter"] = LiveAfter,
        ["liveError"] = LiveError,
        ["steam"] = Steam,
        ["steamError"] = SteamError,
        ["steamChunks"] = SteamChunks,
        ["steamCleared"] = SteamCleared,
        ["persistPath"] = PersistPath,
        ["persistLayer"] = PersistLayer,
        ["persist"] = Persist,
        ["override"] = Override,
        ["overrideSource"] = OverrideSource,
        ["layers"] = Layers,
        ["listOnSteam"] = ListOnSteam,
        ["listOnEos"] = ListOnEos,
    };
}

/// <summary>
/// SERVERDESC1: the server's apply loop, game-free. <see cref="Tick"/> runs each frame: the first tick once the
/// game's settings exist applies the composed text (so a server running Progenitor advertises the signature
/// without anyone opening the page), an edit (<see cref="MarkDirty"/>) applies at once, and every minute the
/// tokens are rendered again; anything is published only when the composed text differs from what was
/// published or from the game's live value. Applying sets the live value (EOS follows), publishes it to Steam
/// (or retries each second until Steam is signed in), clears the Steam keys a longer text left, and writes
/// the text into the highest settings file when it differs there (idempotent: an unchanged file is not
/// rewritten).
/// </summary>
internal sealed class ServerListApplier
{
    private bool _steamPending;
    private DateTime _nextSteamTry = DateTime.MinValue;
    private bool _seeded;
    private string _pendingReason = "start";

    public ServerListPublisher Publisher { get; } = new();

    /// <summary>The last apply (null: none yet).</summary>
    public ServerListReport? Last { get; private set; }

    /// <summary>True while a Steam publish waits for the sign-in.</summary>
    public bool SteamPending => _steamPending;

    /// <summary>An edit or a forced apply: the next tick composes at once.</summary>
    public void MarkDirty(string reason)
    {
        _pendingReason = string.IsNullOrEmpty(reason) ? "edit" : reason;
        Publisher.MarkDirty();
    }

    /// <summary>
    /// One frame. Returns the report when something was published (an apply, or a waiting Steam publish that
    /// went out), else null.
    /// </summary>
    public ServerListReport? Tick(DateTime nowUtc, Func<ServerListValues> values, IServerListGame game)
    {
        if (game == null || values == null) return null;
        bool ready;
        try { ready = game.SettingsReady; }
        catch { ready = false; }
        if (!ready) return null;

        ServerListReport? steamLate = null;
        if (_steamPending && nowUtc >= _nextSteamTry && Last != null)
        {
            _nextSteamTry = nowUtc.AddSeconds(1);
            bool steamReady;
            try { steamReady = game.SteamReady; }
            catch { steamReady = false; }
            if (steamReady)
            {
                PublishSteam(game, Last.Composed, Last);
                steamLate = Last;
            }
        }

        if (!Publisher.Due(nowUtc)) return steamLate;
        string reason = _pendingReason.Length > 0 ? _pendingReason : "minute check";
        _pendingReason = "";

        var v = values();
        string rendered = ServerListDescription.Render(v.Raw, v.Reset, nowUtc, v.Zone);
        var composition = ServerListDescription.Compose(rendered, v.Signature);
        string? live = SafeLive(game);
        if (!_seeded)
        {
            // The game published its own value to Steam at its first pass: that many keys may hold text.
            Publisher.SeedSteam(live);
            _seeded = true;
        }

        if (!Publisher.ShouldPublish(composition.Text, live)) return steamLate;
        return Apply(nowUtc, reason, v, rendered, composition, live, game);
    }

    private ServerListReport Apply(
        DateTime nowUtc, string reason, ServerListValues v, string rendered, Composition composition, string? live, IServerListGame game)
    {
        var report = new ServerListReport
        {
            AtUtc = nowUtc,
            Reason = reason,
            Raw = v.Raw,
            Signature = v.Signature,
            Rendered = rendered,
            Composed = composition.Text,
            Cut = composition.Cut,
            Room = composition.Room,
            LiveBefore = live ?? "",
        };

        try
        {
            game.SetLiveDescription(composition.Text);
        }
        catch (Exception e)
        {
            report.LiveError = e.Message;
        }

        report.LiveAfter = SafeLive(game) ?? "";
        try
        {
            if (game.Listing is { } listing)
            {
                report.ListOnSteam = listing.Steam;
                report.ListOnEos = listing.Eos;
            }
        }
        catch { /* unknown */ }

        Publisher.Record(composition.Text);
        bool steamReady;
        try { steamReady = game.SteamReady; }
        catch { steamReady = false; }
        if (steamReady) PublishSteam(game, composition.Text, report);
        else
        {
            report.Steam = "waiting";
            report.SteamChunks = Publisher.SteamChunks;
            _steamPending = true;
            _nextSteamTry = nowUtc.AddSeconds(1);
        }

        try
        {
            var layers = game.Resolve();
            report.Layers = layers.Describe();
            var target = layers.WriteTarget;
            if (target == null) report.Persist = new PersistOutcome(PersistResult.NoTarget, "no settings file path is known").ToString();
            else
            {
                report.PersistPath = target.Path;
                report.PersistLayer = target.Kind == ServerHostSettingsLayerKind.Save ? "save" : "local";
                report.Persist = ServerListDescription.Persist(target.Path, composition.Text).ToString();
            }

            var found = layers.OverrideOf("Description");
            report.Override = ServerListDescription.OverrideWarning(found);
            report.OverrideSource = found?.Source ?? "";
        }
        catch (Exception e)
        {
            report.Persist = new PersistOutcome(PersistResult.Failed, e.Message).ToString();
        }

        Last = report;
        return report;
    }

    private void PublishSteam(IServerListGame game, string text, ServerListReport report)
    {
        int previous = Publisher.SteamChunks;
        try
        {
            // The game's setter splits into 120-character keys and never clears old ones; an empty text writes
            // nothing, so desc0 is cleared below like any other stale key.
            if (text.Length > 0) game.SteamPublish(text);
            var stale = ServerListDescription.StaleSteamKeys(previous, text);
            foreach (string key in stale) game.SteamSetKey(key, "");
            report.Steam = "published";
            report.SteamError = "";
            report.SteamCleared = stale;
            report.SteamChunks = ServerListDescription.SteamChunks(text);
            Publisher.SteamChunks = report.SteamChunks;
            _steamPending = false;
        }
        catch (Exception e)
        {
            report.Steam = "failed";
            report.SteamError = e.Message;
            _steamPending = false;
        }
    }

    private static string? SafeLive(IServerListGame game)
    {
        try { return game.LiveDescription; }
        catch { return null; }
    }
}
