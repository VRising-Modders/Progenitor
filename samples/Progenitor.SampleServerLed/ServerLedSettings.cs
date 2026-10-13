using System;
using BepInEx.Configuration;
using Progenitor.Ui;

namespace Progenitor.SampleServerLed;

/// <summary>
/// Binds one cfg entry. <c>Plugin</c> binds through <c>ctx.Bind</c> (with real
/// <c>AcceptableValue*</c> bounds and <c>synced</c>); the tests bind through the config-file shim.
/// </summary>
internal interface IEntryBinder
{
    ConfigEntry<int> Int(string section, string key, int value, string description, bool synced, int min, int max);

    ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null);

    ConfigEntry<bool> Flag(string section, string key, bool value, string description);
}

/// <summary>
/// The server-led sample's cfg entries, in the three groups its mod page shows. The server binds only
/// the server options; the client binds all of them (the server options synced, so the server's values
/// arrive through ConfigSync and an administrator can change them from the page).
/// </summary>
internal sealed class ServerLedEntries
{
    internal const string ServerSection = "Server";
    internal const string ForServerSection = "ForServer";
    internal const string ClientSection = "Client";

    private ServerLedEntries(ConfigEntry<int> announceMinutes, ConfigEntry<string> greeting)
    {
        AnnounceMinutes = announceMinutes;
        Greeting = greeting;
    }

    /// <summary>Server option: minutes between announcements (server cfg; synced; admin-editable).</summary>
    internal ConfigEntry<int> AnnounceMinutes { get; }

    /// <summary>Server option: the text every announcement starts with (server cfg; synced; admin-editable).</summary>
    internal ConfigEntry<string> Greeting { get; }

    /// <summary>Client option for the server: this player wants announcements. Client only.</summary>
    internal ConfigEntry<bool>? ReceiveAnnouncements { get; private set; }

    /// <summary>Client option for the server: <c>Chat</c> or <c>Popup</c>. Client only.</summary>
    internal ConfigEntry<string>? Delivery { get; private set; }

    /// <summary>Client option for the client: seconds before a popup closes itself (0 = until OK). Client only.</summary>
    internal ConfigEntry<int>? PopupSeconds { get; private set; }

    /// <summary>Client option for the client: also write each announcement to the client log. Client only.</summary>
    internal ConfigEntry<bool>? LogAnnouncements { get; private set; }

    /// <summary>Client option for the client: log whether each joined server runs the server part. Client only.</summary>
    internal ConfigEntry<bool>? JoinCheck { get; private set; }

    /// <summary>Bind this side's entries. Bind before <c>ctx.Settings.Page</c> (a synced entry is opted into admin edits there).</summary>
    internal static ServerLedEntries Bind(IEntryBinder bind, bool isClient)
    {
        if (bind == null) throw new ArgumentNullException(nameof(bind));
        var entries = new ServerLedEntries(
            bind.Int(ServerSection, "AnnounceMinutes", ServerLedPolicy.DefaultMinutes,
                "Minutes between server announcements. Set on the server.", synced: true,
                ServerLedPolicy.MinMinutes, ServerLedPolicy.MaxMinutes),
            bind.Text(ServerSection, "Greeting", ServerLedPolicy.DefaultGreeting,
                "The text every server announcement starts with. Set on the server.", synced: true));
        if (!isClient) return entries;

        entries.ReceiveAnnouncements = bind.Flag(ForServerSection, "ReceiveAnnouncements", true,
            "Sent to the server: whether it announces to you.");
        entries.Delivery = bind.Text(ForServerSection, "Delivery", ServerLedPolicy.DeliveryChat,
            "Sent to the server: Chat (a system chat line) or Popup (shown by this client part).",
            synced: false, ServerLedPolicy.DeliveryChoices);
        entries.PopupSeconds = bind.Int(ClientSection, "PopupSeconds", ServerLedPolicy.DefaultPopupSeconds,
            "Seconds before an announcement popup closes itself; 0 keeps it until you press OK.", synced: false,
            ServerLedPolicy.MinPopupSeconds, ServerLedPolicy.MaxPopupSeconds);
        entries.LogAnnouncements = bind.Flag(ClientSection, "LogAnnouncements", true,
            "Also write each announcement to the client log.");
        entries.JoinCheck = bind.Flag(ClientSection, "JoinCheck", true,
            "On each join, write one log line saying whether the server runs the server part.");
        return entries;
    }
}

/// <summary>
/// The server-led sample's mod page (<c>ctx.Settings.Page</c>): three sections, one per kind of
/// option. Game-free: the tests build the same page and read its scopes and grayed rows.
/// </summary>
internal static class ServerLedSettings
{
    internal const string SectionServer = "Server options (for the server)";
    internal const string SectionForServer = "Client options for the server";
    internal const string SectionForClient = "Client options for the client";

    internal const string FallbackServer =
        "This server does not run the server part, so there is nothing to set here.";

    internal const string FallbackForServer =
        "Without the server part there are no announcements, so this is not sent.";

    internal const string FallbackPopup =
        "Announcements come only from the server part, so nothing is shown.";

    /// <summary>
    /// Lay out the page. On the server only <see cref="SectionServer"/> is built: the server has no
    /// page to draw, and the call is what opts its synced entries into administrator edits.
    /// </summary>
    /// <param name="page">The builder.</param>
    /// <param name="entries">This side's entries.</param>
    /// <param name="serverPart">The live "Server part" line (client only; null on the server).</param>
    internal static void Build(SettingsPageBuilder page, ServerLedEntries entries, Func<string>? serverPart)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        page.Section(SectionServer)
            .Slider(entries.AnnounceMinutes, ServerLedPolicy.MinMinutes, ServerLedPolicy.MaxMinutes, 1, "min",
                help: "How often the server announces. Stored in the server's cfg; an administrator can change it here while joined.",
                needsServerPart: true, fallback: FallbackServer, scope: SettingScope.Server)
            .Text(entries.Greeting,
                help: "What every announcement says first. Stored in the server's cfg; an administrator can change it here while joined.",
                needsServerPart: true, fallback: FallbackServer, scope: SettingScope.Server);

        if (entries.ReceiveAnnouncements == null || entries.Delivery == null || entries.PopupSeconds == null
            || entries.LogAnnouncements == null || entries.JoinCheck == null)
            return;

        page.Section(SectionForServer)
            .Toggle(entries.ReceiveAnnouncements,
                help: "Sent to the server, which leaves you out of its announcements when this is off.",
                needsServerPart: true, fallback: FallbackForServer)
            .Radio(entries.Delivery, ServerLedPolicy.DeliveryChoices,
                help: "Sent to the server: a system chat line, or a popup this client part shows.",
                needsServerPart: true, fallback: FallbackForServer)
            .Section(SectionForClient)
            .Slider(entries.PopupSeconds, ServerLedPolicy.MinPopupSeconds, ServerLedPolicy.MaxPopupSeconds, 1, "s",
                help: "How long an announcement popup stays; 0 keeps it until you press OK. Used by this client only.",
                needsServerPart: true, fallback: FallbackPopup, scope: SettingScope.Local)
            .Toggle(entries.LogAnnouncements,
                help: "Write each announcement to this client's log too.",
                needsServerPart: true, fallback: FallbackPopup, scope: SettingScope.Local)
            .Toggle(entries.JoinCheck,
                help: "On each join, log whether the server runs the server part. Works on any server.",
                scope: SettingScope.Local);

        if (serverPart != null)
            page.Info("Server part", serverPart, 1,
                help: "What this client knows from the joined server's handshake.");
    }
}
