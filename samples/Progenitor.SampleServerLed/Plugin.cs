using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Dev;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.SampleServerLed;

/// <summary>
/// A mostly-server mod with an optional client part (docs/mod-settings.md, "Counterparts"; the
/// README beside this file has the join matrix and the acceptance procedure).
///
/// <list type="bullet">
/// <item><b>Server part (the mod).</b> Announces to every connected player every
/// <c>[Server] AnnounceMinutes</c> minutes, starting with <c>[Server] Greeting</c>. <c>.serverled
/// status</c> reports what it does for the caller; <c>.serverled announce</c> (admin) announces now.
/// No unit, item or world state is touched.</item>
/// <item><b>Client part (optional).</b> Sends this player's preferences (receive or not, chat line or
/// popup) to the server as a versioned message (<c>ctx.Messages</c>), and shows popup announcements.
/// Vanilla clients and clients without it join and get chat lines.</item>
/// </list>
///
/// <para><b>Negotiation.</b> The server part offers the feature <c>prefs</c> and the client part
/// <c>popups</c> (<see cref="ServerLedMessages"/>). The client sends preferences only to a server whose
/// part offers <c>prefs</c>; the server sends a popup only to a client that offers <c>popups</c>, and a
/// chat line otherwise.</para>
///
/// <para><b>Declaration.</b> <c>[Counterpart(Optional, ClientExtras = …)]</c> lowers the
/// <c>ServerMustHave</c> row to <c>NoNeedForSync</c> on both sides: the server never refuses a client
/// without the client part, and the client part is never refused or paused on a server without the
/// server part. Two sides that both run it work together across every 1.x
/// (<see cref="ServerLedDeclaration.Peers"/>); a Progenitor older than ranges holds them to major.minor.</para>
/// </summary>
[BepInPlugin(ServerLedDeclaration.Guid, ServerLedDeclaration.Name, ServerLedDeclaration.Version)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(ServerLedDeclaration.Level, ServerLedDeclaration.Rule, Peers = ServerLedDeclaration.Peers)]
[Counterpart(ServerLedDeclaration.Need, ClientExtras = ServerLedDeclaration.ClientExtras,
    Features = new[] { ServerLedDeclaration.FeatureAnnouncements })]
public sealed class Plugin : ProgenitorPlugin<ServerLedContext>
{
    /// <inheritdoc />
    protected override void OnLoad(ServerLedContext ctx)
    {
        var entries = ServerLedEntries.Bind(new ContextBinder(ctx), ctx.IsClient);
        Func<string>? serverPart = ctx.IsClient
            ? () => ServerLedPolicy.PresenceText(ctx.Handshake.ServerPart)
            : null;
        ctx.Settings.Page(page => ServerLedSettings.Build(page, entries, serverPart));

        if (ctx.IsServer) new ServerHalf(ctx, entries).Attach();
        else new ClientHalf(ctx, entries).Attach();
    }

    /// <inheritdoc />
    protected override void OnUnload(ServerLedContext ctx, PluginUnloadReason reason)
    {
        // While the context can still send: tell the server part this client part is going, so it
        // falls back to a chat line at once instead of after the stale window.
        ClientHalf.Active?.SayGoodbye();
    }
}

/// <summary>The sample's context. Empty, as every context should be.</summary>
public sealed class ServerLedContext : PluginContext { }

/// <summary><see cref="IEntryBinder"/> over <c>ctx.Bind</c>, with real <c>AcceptableValue*</c> bounds.</summary>
internal sealed class ContextBinder : IEntryBinder
{
    private readonly PluginContext _ctx;

    internal ContextBinder(PluginContext ctx) => _ctx = ctx;

    public ConfigEntry<int> Int(string section, string key, int value, string description, bool synced, int min, int max) =>
        _ctx.Bind(section, key, value, description, synced, new AcceptableValueRange<int>(min, max));

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

/// <summary>
/// The server part: the announcement loop, the per-player preference table and the commands. Every
/// registration is tracked on the context; the loop stops on unload.
/// </summary>
internal sealed class ServerHalf
{
    private static ServerHalf? _active;

    private readonly ServerLedContext _ctx;
    private readonly ServerLedEntries _entries;
    private readonly ServerLedPolicy.PrefsTable _prefs = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _processStart;
    private double _lastAnnounce;
    private int _sequence;
    private bool _stopped;

    internal ServerHalf(ServerLedContext ctx, ServerLedEntries entries)
    {
        _ctx = ctx;
        _entries = entries;
        try { _processStart = Process.GetCurrentProcess().StartTime; }
        catch { _processStart = DateTime.Now; }
    }

    /// <summary>The attached server part, for the static command methods.</summary>
    internal static ServerHalf? Active => _active;

    private double Now => _clock.Elapsed.TotalSeconds;

    internal void Attach()
    {
        _active = this;
        _ctx.Track(new OnDispose(Stop));
        // Clients ask for this before they send preferences; unload withdraws it (tracked).
        _ctx.Features.Offer(ServerLedMessages.FeaturePrefs);
        _ctx.Messages.On<PrefsMessage>(OnPrefs);
        _ctx.Events.On<PlayerDisconnected>(OnLeft);
        _ctx.Commands.Register(typeof(ServerLedCommands));
        _lastAnnounce = Now;
        MainThread.PostDelayed(ServerLedPolicy.TickSeconds, Tick, _ctx.Owner);
        _ctx.Log.LogInfo("Server-led sample: server part loaded; announcing every "
            + ((int)(ServerLedPolicy.IntervalSeconds(_entries.AnnounceMinutes.Value) / 60)) + " min. Clients without the client part are welcome.");
    }

    private void Stop()
    {
        _stopped = true;
        _prefs.Clear();
        if (ReferenceEquals(_active, this)) _active = null;
    }

    private void Tick()
    {
        if (_stopped) return;
        try
        {
            if (ServerLedPolicy.IsDue(Now, _lastAnnounce, _entries.AnnounceMinutes.Value)) Announce();
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Server-led sample: announcement tick threw: " + e);
        }

        if (!_stopped) MainThread.PostDelayed(ServerLedPolicy.TickSeconds, Tick, _ctx.Owner);
    }

    /// <summary>Announce to every connected player by their preferences. Returns the log line.</summary>
    internal string Announce()
    {
        _lastAnnounce = Now;
        var world = _ctx.World;
        if (world == null) return "Server-led sample: the world is not ready; nothing announced.";

        var em = world.EntityManager;
        var connected = new List<(Entity Entity, User User)>();
        var users = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var user = em.GetComponentData<User>(users[i]);
            if (user.IsConnected) connected.Add((users[i], user));
        }

        int uptime = (int)Math.Max(0, (DateTime.Now - _processStart).TotalMinutes);
        int sequence = ++_sequence;
        string text = ServerLedPolicy.AnnouncementText(_entries.Greeting.Value, connected.Count, uptime);
        // The popup carries the chat line's exact text, never a client-side recomposition.
        var message = new AnnounceMessage { Sequence = sequence, Text = text };
        double now = Now;

        int chat = 0, popup = 0, skipped = 0;
        foreach (var (entity, user) in connected)
        {
            bool showsPopups = _ctx.Peers.Of(entity).Has(ServerLedMessages.FeaturePopups);
            switch (ServerLedPolicy.RouteFor(_prefs.For(user.Index, now), showsPopups))
            {
                case ServerLedPolicy.Route.Popup:
                    // Anything but Sent (the client part just unloaded, the connection is going) falls back to the chat line.
                    if (_ctx.Messages.SendTo(entity, message) == SendResult.Sent) popup++;
                    else if (_ctx.Chat.SendTo(entity, text)) chat++;
                    break;
                case ServerLedPolicy.Route.Chat:
                    if (_ctx.Chat.SendTo(entity, text)) chat++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        string line = ServerLedPolicy.AnnouncedLine(sequence, chat, popup, skipped);
        _ctx.Log.LogInfo(line);
        return line;
    }

    /// <summary>The <c>.serverled status</c> lines for the caller.</summary>
    internal IReadOnlyList<string> Status(int userIndex) =>
        ServerLedPolicy.StatusLines(_entries.AnnounceMinutes.Value, _entries.Greeting.Value, _prefs.For(userIndex, Now), _prefs.CountAt(Now));

    private void OnPrefs(Entity userEntity, PrefsMessage message, MessageInfo info)
    {
        try
        {
            if (_stopped) return;
            var world = _ctx.World;
            if (world == null || userEntity == Entity.Null) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            double now = Now;
            string sender = user.CharacterName.ToString();
            var verdict = ServerLedPolicy.Admit(message, Handshake.UserAnswerStatus(user.Index), _prefs.SecondsSince(user.Index, now));
            if (verdict != ServerLedPolicy.Verdict.Ok)
            {
                // A heartbeat that arrives early is not worth a line; every other refusal is.
                if (verdict != ServerLedPolicy.Verdict.TooSoon) _ctx.Log.LogInfo(ServerLedPolicy.RefusedLine(sender, verdict));
                return;
            }

            if (message.Kind == ServerLedPolicy.KindGone)
            {
                if (_prefs.Forget(user.Index))
                    _ctx.Log.LogInfo("Server-led sample: " + sender + "'s client part unloaded; using the server defaults.");
                return;
            }

            var prefs = ServerLedPolicy.FromMessage(message);
            bool changed = _prefs.Set(user.Index, prefs, now);
            if (changed) _ctx.Log.LogInfo(ServerLedPolicy.AcceptedLine(sender, prefs, changed));
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Server-led sample: preference receive threw: " + e);
        }
    }

    private void OnLeft(PlayerDisconnected ev)
    {
        try { if (ev != null) _prefs.Forget(ev.User.Index); }
        catch (Exception e) { _ctx.Log.LogError("Server-led sample: disconnect handler threw: " + e); }
    }
}

/// <summary><c>.serverled status</c> and <c>.serverled announce</c>. Server only.</summary>
[CommandGroup("serverled")]
internal static class ServerLedCommands
{
    [Command("status", description: "What the server-led sample announces, and what it does for you.")]
    public static void Status(ICommandContext ctx)
    {
        var half = ServerHalf.Active;
        if (half == null) { ctx.Reply("Server-led sample: not running."); return; }
        foreach (string line in half.Status(ctx.User.Index)) ctx.Reply(line);
    }

    [Command("announce", adminOnly: true, description: "Send the server-led sample's announcement now.")]
    public static void Announce(ICommandContext ctx)
    {
        var half = ServerHalf.Active;
        if (half == null) { ctx.Reply("Server-led sample: not running."); return; }
        ctx.Reply(half.Announce());
    }
}

/// <summary>
/// The optional client part: sends this player's preferences to the server part (only when it offers
/// <c>prefs</c> and the join is accepted), and shows popup announcements (it offers <c>popups</c>).
/// </summary>
internal sealed class ClientHalf
{
    /// <summary>Process-wide key of the join whose check line was written; survives Core swaps and plugin reloads (a plain string).</summary>
    private const string JoinCheckSlot = "progenitor.sampleserverled.joincheck";

    private static ClientHalf? _active;

    private readonly ServerLedContext _ctx;
    private readonly ServerLedEntries _entries;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PrefsMessage? _lastSent;
    private double _lastSendAt;
    private CounterpartPresence _lastPresence = CounterpartPresence.Unknown;
    private IDisposable? _popup;
    private bool _stopped;

    internal ClientHalf(ServerLedContext ctx, ServerLedEntries entries)
    {
        _ctx = ctx;
        _entries = entries;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    internal void Attach()
    {
        _active = this;
        _ctx.Track(new OnDispose(() =>
        {
            _stopped = true;
            ClosePopup();
            if (ReferenceEquals(_active, this)) _active = null;
        }));
        // The server sends popups only to clients that offer this; unload withdraws it (tracked).
        _ctx.Features.Offer(ServerLedMessages.FeaturePopups);
        _ctx.Messages.On<AnnounceMessage>((from, message, info) => OnAnnounce(message));
        MainThread.PostDelayed(1.0, Poll, _ctx.Owner);
    }

    /// <summary>The attached client part, for <c>Plugin.OnUnload</c>.</summary>
    internal static ClientHalf? Active => _active;

    /// <summary>Send the goodbye message when the server part has this player's preferences. Never throws.</summary>
    internal void SayGoodbye()
    {
        try
        {
            if (_lastSent == null) return;
            if (!_ctx.Peer.Has(ServerLedMessages.FeaturePrefs)) return;
            if (!string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal)) return;
            if (_ctx.Messages.SendToServer(ServerLedPolicy.GoneMessage()) == SendResult.Sent) _lastSent = null;
        }
        catch (Exception e) { _ctx.Log.LogWarning("Server-led sample: goodbye message threw: " + e.Message); }
    }

    private void Poll()
    {
        if (_stopped) return;
        try
        {
            var presence = _ctx.Handshake.ServerPart;
            bool accepted = string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);
            if (presence != _lastPresence)
            {
                _lastPresence = presence;
                // Once per join, keyed on the client world, not on this generation's view of it.
                string? last = AppDomain.CurrentDomain.GetData(JoinCheckSlot) as string;
                if (_entries.JoinCheck!.Value
                    && ServerLedPolicy.ShouldLogJoinCheck(ClientWorldEpoch.ObservedSequence, presence, last, out string key)
                    && ServerLedPolicy.JoinCheckLine(presence) is { } line)
                {
                    AppDomain.CurrentDomain.SetData(JoinCheckSlot, key);
                    _ctx.Log.LogInfo(line);
                }
            }

            // Ask the server part, not only the handshake: it reads preferences when it offers "prefs".
            // A new join (or a server part that went away) starts over: the server forgot this player.
            bool serverTakesPrefs = _ctx.Peer.Has(ServerLedMessages.FeaturePrefs);
            if (!serverTakesPrefs || !accepted) _lastSent = null;

            var current = ServerLedPolicy.ToMessage(_entries.ReceiveAnnouncements!.Value, _entries.Delivery!.Value);
            if (ServerLedPolicy.ShouldSend(serverTakesPrefs, accepted, current, _lastSent, Now - _lastSendAt)
                && _ctx.Messages.SendToServer(current) == SendResult.Sent)
            {
                _lastSent = current;
                _lastSendAt = Now;
            }
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Server-led sample: preference poll threw: " + e);
        }

        if (!_stopped) MainThread.PostDelayed(1.0, Poll, _ctx.Owner);
    }

    private void OnAnnounce(AnnounceMessage message)
    {
        try
        {
            if (_stopped || ServerLedPolicy.PopupText(message) is not { } text) return;

            if (_entries.LogAnnouncements!.Value) _ctx.Log.LogInfo("Server-led sample: announcement: " + text);

            ClosePopup();
            var popup = _ctx.Dialogs.Notice(ServerLedPolicy.PopupTitle, text);
            _popup = popup;
            int seconds = Math.Clamp(_entries.PopupSeconds!.Value, ServerLedPolicy.MinPopupSeconds, ServerLedPolicy.MaxPopupSeconds);
            if (seconds > 0)
                MainThread.PostDelayed(seconds, () =>
                {
                    if (!ReferenceEquals(_popup, popup)) return;
                    ClosePopup();
                }, _ctx.Owner);
        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Server-led sample: announcement receive threw: " + e);
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
