using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Dev;
using Progenitor.Ecs;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Players;
using Progenitor.Ui;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.SampleServerLed;

/// <summary>
/// Server announcements with persistent personal preferences and an optional popup client.
/// Receive and Delivery are server-owned PlayerSetting values, keyed by platform account.
/// Core publishes their controls and handles personal writes; this sample sends only announcements.
/// The optional counterpart declaration allows players without the popup client to join.
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
        // Core owns the handles and persistence. No client cfg entries or custom preference packets.
        PlayerSetting<bool> receive = ctx.Settings.PerPlayer("ForServer", "ReceiveAnnouncements", true,
            description: "Receive this server's announcements.");
        PlayerSetting<string> delivery = ctx.Settings.PerPlayer("ForServer", "Delivery", ServerLedPolicy.DeliveryChat,
            description: "Chat, or Popup when your client has the optional sample installed.",
            acceptableValues: new AcceptableValueList<string>(ServerLedPolicy.DeliveryChoices));
        Func<string>? serverPart = ctx.IsClient
            ? () => ServerLedPolicy.PresenceText(ctx.Handshake.ServerPart)
            : null;
        ctx.Settings.Page(page => ServerLedSettings.Build(page, entries, serverPart));

        if (ctx.IsServer) new ServerHalf(ctx, entries, receive, delivery).Attach();
        else new ClientHalf(ctx, entries).Attach();
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
        synced
            ? _ctx.Bind(section, key, value, scope: SettingScope.ServerWide, description: description,
                acceptableValues: new AcceptableValueRange<int>(min, max))
            : _ctx.Bind(section, key, value, description, acceptableValues: new AcceptableValueRange<int>(min, max));

    public ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null) =>
        synced
            ? _ctx.Bind(section, key, value, scope: SettingScope.ServerWide, description: description,
                acceptableValues: choices == null ? null : new AcceptableValueList<string>(choices))
            : _ctx.Bind(section, key, value, description,
                acceptableValues: choices == null ? null : new AcceptableValueList<string>(choices));

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
/// The server part: the announcement loop, persisted per-account preferences and the commands. Every
/// registration is tracked on the context; the loop stops on unload.
/// </summary>
internal sealed class ServerHalf
{
    private static ServerHalf? _active;

    private readonly ServerLedContext _ctx;
    private readonly ServerLedEntries _entries;
    private readonly PlayerSetting<bool> _receive;
    private readonly PlayerSetting<string> _delivery;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _processStart;
    private double _lastAnnounce;
    private int _sequence;
    private bool _stopped;

    internal ServerHalf(ServerLedContext ctx, ServerLedEntries entries, PlayerSetting<bool> receive, PlayerSetting<string> delivery)
    {
        _ctx = ctx;
        _entries = entries;
        _receive = receive;
        _delivery = delivery;
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
        _ctx.Commands.Register(typeof(ServerLedCommands));
        _lastAnnounce = Now;
        MainThread.PostDelayed(ServerLedPolicy.TickSeconds, Tick, _ctx.Owner);
        _ctx.Log.LogInfo("Server-led sample: server part loaded; announcing every "
            + ((int)(ServerLedPolicy.IntervalSeconds(_entries.AnnounceMinutes.Value) / 60)) + " min. Clients without the client part are welcome.");
    }

    private void Stop()
    {
        _stopped = true;
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

        int chat = 0, popup = 0, skipped = 0;
        foreach (var (entity, user) in connected)
        {
            bool showsPopups = _ctx.Peers.Of(entity).Has(ServerLedMessages.FeaturePopups);
            switch (ServerLedPolicy.RouteFor(Preferences(user.PlatformId), showsPopups))
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
    internal IReadOnlyList<string> Status(ulong platformId) =>
        ServerLedPolicy.StatusLines(_entries.AnnounceMinutes.Value, _entries.Greeting.Value, Preferences(platformId));

    private ServerLedPolicy.PlayerPrefs Preferences(ulong platformId) =>
        ServerLedPolicy.PreferencesFor(platformId,
            id => _receive.For(new PlayerAccountId(id)),
            id => _delivery.For(new PlayerAccountId(id)));
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
        foreach (string line in half.Status(ctx.User.PlatformId)) ctx.Reply(line);
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
/// The optional client part shows popup announcements and logs server-part availability.
/// Core's scoped settings page handles personal preference edits, even without this client part.
/// </summary>
internal sealed class ClientHalf
{
    /// <summary>Process-wide key of the join whose check line was written; survives Core swaps and plugin reloads (a plain string).</summary>
    private const string JoinCheckSlot = "progenitor.sampleserverled.joincheck";


    private readonly ServerLedContext _ctx;
    private readonly ServerLedEntries _entries;
    private CounterpartPresence _lastPresence = CounterpartPresence.Unknown;
    private IDisposable? _popup;
    private bool _stopped;

    internal ClientHalf(ServerLedContext ctx, ServerLedEntries entries)
    {
        _ctx = ctx;
        _entries = entries;
    }

    internal void Attach()
    {
        _ctx.Track(new OnDispose(() =>
        {
            _stopped = true;
            ClosePopup();
        }));
        // The server sends popups only to clients that offer this; unload withdraws it (tracked).
        _ctx.Features.Offer(ServerLedMessages.FeaturePopups);
        _ctx.Messages.On<AnnounceMessage>((from, message, info) => OnAnnounce(message));
        MainThread.PostDelayed(1.0, Poll, _ctx.Owner);
    }

    private void Poll()
    {
        if (_stopped) return;
        try
        {
            var presence = _ctx.Handshake.ServerPart;
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


        }
        catch (Exception e)
        {
            _ctx.Log.LogError("Server-led sample: availability poll threw: " + e);
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
