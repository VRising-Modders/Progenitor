using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using ProjectM.Network;
using Progenitor.Dev;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Unity.Entities;

namespace Progenitor.SampleClientLed;

/// <summary>
/// A mostly-client mod with an optional server part (docs/mod-settings.md, "Counterparts"; the
/// README beside this file has the join matrix and the acceptance procedure).
///
/// <list type="bullet">
/// <item><b>Client part (the mod).</b> A clock: Ctrl+F6 opens a small popup with your local time, and
/// the mod page shows it live. Works on any server.</item>
/// <item><b>Server part (optional, tiny).</b> Answers the client's time request with one read of the
/// world clock (<c>ctx.Time.GetTimeOfDay()</c>) and holds the server's default clock format. With it,
/// the clock adds the in-game time and the next sunrise or sunset, and <c>Server default</c> follows
/// the server. It changes nothing in the world.</item>
/// </list>
///
/// <para><b>Negotiation.</b> The server part offers the feature <c>time</c>
/// (<see cref="ClientLedMessages"/>); the client sends its <see cref="TimeRequest"/> only to a server
/// that offers it, and otherwise shows its local estimate.</para>
///
/// <para><b>Declaration.</b> <c>[NetworkCompatibility(NoNeedForSync, Minor, Peers = "&gt;=1.0.0 &lt;2.0.0")]</c> and
/// <c>[Counterpart(Limited, Features = …)]</c>: neither side requires the other, so a server without
/// the server part never refuses or pauses the client part, a server with it never refuses a client
/// without it, and two sides that both run it work together across every 1.x (a Progenitor older
/// than ranges holds them to major.minor). The counterpart
/// declaration makes the mod page two-sided, says <c>Server part not on this server · limited</c> on
/// a server without it and grays the rows marked <c>needsServerPart</c>. Why not
/// <c>ServerMustHave</c>: see <see cref="ClientLedDeclaration.Level"/>.</para>
/// </summary>
[BepInPlugin(ClientLedDeclaration.Guid, ClientLedDeclaration.Name, ClientLedDeclaration.Version)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(ClientLedDeclaration.Level, ClientLedDeclaration.Rule, Peers = ClientLedDeclaration.Peers)]
[Counterpart(ClientLedDeclaration.Need,
    Features = new[] { ClientLedDeclaration.FeatureServerTime, ClientLedDeclaration.FeatureServerDefault })]
public sealed class Plugin : ProgenitorPlugin<ClientLedContext>
{
    /// <inheritdoc />
    protected override void OnLoad(ClientLedContext ctx)
    {
        var entries = ClientLedEntries.Bind(new ContextBinder(ctx), ctx.IsClient);
        if (ctx.IsServer)
        {
            ctx.Settings.Page(page => ClientLedSettings.Build(page, entries, null, null));
            new ServerHalf(ctx).Attach();
            return;
        }

        var client = new ClientHalf(ctx, entries);
        ctx.Settings.Page(page => ClientLedSettings.Build(
            page, entries, client.LocalTimeText, client.ServerTimeText, () => ClientLedPolicy.PresenceText(ctx.Handshake.ServerPart)));
        client.Attach();
    }
}

/// <summary>The sample's context. Empty, as every context should be.</summary>
public sealed class ClientLedContext : PluginContext { }

/// <summary><see cref="IEntryBinder"/> over <c>ctx.Bind</c>, with a real <c>AcceptableValueList</c>.</summary>
internal sealed class ContextBinder : IEntryBinder
{
    private readonly PluginContext _ctx;

    internal ContextBinder(PluginContext ctx) => _ctx = ctx;

    public ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null) =>
        choices == null
            ? _ctx.Bind(section, key, value, description, synced)
            : _ctx.Bind(section, key, value, description, synced, new AcceptableValueList<string>(choices));

    public ConfigEntry<bool> Flag(string section, string key, bool value, string description) =>
        _ctx.Bind(section, key, value, description);
}

/// <summary>A disposable that runs one action once.</summary>
internal sealed class OnDispose : IDisposable
{
    private Action? _action;

    internal OnDispose(Action action) => _action = action;

    public void Dispose()
    {
        var action = _action;
        _action = null;
        action?.Invoke();
    }
}

/// <summary>The tiny optional server part: answers time requests. Tracked on the context.</summary>
internal sealed class ServerHalf
{
    private readonly ClientLedContext _ctx;
    private readonly ClientLedPolicy.RequestTable _requests = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _processStart;
    private bool _stopped;

    internal ServerHalf(ClientLedContext ctx)
    {
        _ctx = ctx;
        try { _processStart = Process.GetCurrentProcess().StartTime; }
        catch { _processStart = DateTime.Now; }
    }

    internal void Attach()
    {
        _ctx.Track(new OnDispose(() => { _stopped = true; _requests.Clear(); }));
        // Clients ask for this before they send a request; unload withdraws it (tracked).
        _ctx.Features.Offer(ClientLedMessages.FeatureTime);
        _ctx.Messages.On<TimeRequest>(OnRequest);
        _ctx.Events.On<PlayerDisconnected>(ev => { if (ev != null) _requests.Forget(ev.User.Index); });
        _ctx.Log.LogInfo("Client-led sample: server part loaded; answering clock requests.");
    }

    private void OnRequest(Entity userEntity, TimeRequest request, MessageInfo info)
    {
        try
        {
            if (_stopped) return;
            var world = _ctx.World;
            if (world == null || userEntity == Entity.Null) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            double now = _clock.Elapsed.TotalSeconds;
            var verdict = ClientLedPolicy.Admit(Handshake.UserAnswerStatus(user.Index), _requests.SecondsSince(user.Index, now));
            if (verdict != ClientLedPolicy.Verdict.Ok)
            {
                if (verdict != ClientLedPolicy.Verdict.TooSoon)
                    _ctx.Log.LogInfo(ClientLedPolicy.RefusedLine(user.CharacterName.ToString(), verdict));
                return;
            }

            _requests.Note(user.Index, now);
            var reading = _ctx.Time.GetTimeOfDay();
            if (reading is not { } clock) return;   // the world clock is not ready: the client keeps its fallback
            var answer = new TimeAnswer
            {
                Sequence = request.Sequence,
                Day = clock.Day,
                Hour = clock.Hour,
                DayLengthSeconds = clock.DayLengthSeconds,
                SunriseHour = clock.SunriseHour,
                SunsetHour = clock.SunsetHour,
                UptimeMinutes = (int)Math.Max(0, (DateTime.Now - _processStart).TotalMinutes),
            };
            _ctx.Messages.SendTo(userEntity, answer);
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Client-led sample: time request threw: " + e);
        }
    }
}

/// <summary>
/// The client part: the clock, its popup and — only while the server part offers <c>time</c> and the
/// join is accepted — the time requests.
/// </summary>
internal sealed class ClientHalf
{
    private readonly ClientLedContext _ctx;
    private readonly ClientLedEntries _entries;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeAnswer? _answer;
    private double _answerAt;
    private double _requestAt = double.NegativeInfinity;
    private int _sequence;
    private bool _pressWaiting;
    private double _pressAt;
    private IDisposable? _popup;
    private bool _stopped;

    internal ClientHalf(ClientLedContext ctx, ClientLedEntries entries)
    {
        _ctx = ctx;
        _entries = entries;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private CounterpartPresence Presence => _ctx.Handshake.ServerPart;

    /// <summary>The joined server's part answers time requests (it offers the feature).</summary>
    private bool ServerAnswers => _ctx.Peer.Has(ClientLedMessages.FeatureTime);

    private string Format => ClientLedPolicy.EffectiveFormat(_entries.Format!.Value, Presence, _entries.ServerDefaultFormat.Value);

    internal void Attach()
    {
        _ctx.Track(new OnDispose(() => { _stopped = true; ClosePopup(); }));
        _ctx.Messages.On<TimeAnswer>((from, answer, info) => OnAnswer(answer));
        _ctx.Keybinds.Register(ClientLedSettings.KeybindId, new KeyChord(UiKey.F6, KeyModifiers.Ctrl),
            "Show the clock (local time; with the server part, the in-game time too).", OnPress);
        MainThread.PostDelayed(1.0, Poll, _ctx.Owner);
    }

    /// <summary>The page's live "Local time" line.</summary>
    internal string LocalTimeText() => ClientLedPolicy.FormatLocal(DateTime.Now, Format, _entries.ShowSeconds!.Value);

    /// <summary>The page's live "In-game time" line: the estimate, or the fallback.</summary>
    internal string ServerTimeText() => ServerTime() ?? ClientLedPolicy.LocalEstimateLine;

    private string? ServerTime()
    {
        if (!ServerAnswers || _answer is not { } answer) return null;
        return ClientLedPolicy.ServerTimeLine(answer, Now - _answerAt, Format);
    }

    private void Poll()
    {
        if (_stopped) return;
        try
        {
            bool serverAnswers = ServerAnswers;
            bool accepted = string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);
            // A join without the server part (or no join) keeps no answer: it belonged to another server.
            if (!serverAnswers || !accepted) _answer = null;

            double? age = _answer == null ? null : Now - _answerAt;
            bool wantsServer = _entries.ShowServerTime!.Value;
            if (wantsServer && ClientLedPolicy.ShouldRequest(serverAnswers, accepted, Now - _requestAt, age, _pressWaiting)
                && _ctx.Messages.SendToServer(new TimeRequest { Sequence = ++_sequence }) == SendResult.Sent)
                _requestAt = Now;

            if (_pressWaiting && Now - _pressAt >= ClientLedPolicy.PopupWaitSeconds) ShowPopup();
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Client-led sample: clock poll threw: " + e);
        }

        if (!_stopped) MainThread.PostDelayed(_pressWaiting ? 0.25 : 1.0, Poll, _ctx.Owner);
    }

    private void OnPress()
    {
        try
        {
            bool accepted = string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);
            if (_entries.ShowServerTime!.Value && ServerAnswers && accepted)
            {
                // Ask for a fresh answer; the popup opens when it arrives or after a short wait.
                _pressWaiting = true;
                _pressAt = Now;
                return;
            }

            ShowPopup();
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Client-led sample: clock key threw: " + e);
        }
    }

    private void ShowPopup()
    {
        _pressWaiting = false;
        string body = ClientLedPolicy.PopupBody(LocalTimeText(), ServerTime(), _entries.ShowServerTime!.Value);
        ClosePopup();
        _popup = _ctx.Dialogs.Notice(ClientLedPolicy.PopupTitle, body);
        _ctx.Log.LogInfo("Client-led sample: clock: " + body.Replace('\n', ' '));
    }

    private void OnAnswer(TimeAnswer answer)
    {
        try
        {
            if (_stopped || answer == null) return;
            _answer = answer;
            _answerAt = Now;
            if (_pressWaiting) ShowPopup();
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Client-led sample: time answer threw: " + e);
        }
    }

    private void ClosePopup()
    {
        var popup = _popup;
        _popup = null;
        try { popup?.Dispose(); }
        catch { /* the dialog is already gone */ }
    }
}
