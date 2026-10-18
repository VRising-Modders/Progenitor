using System;
using BepInEx.Configuration;
using Progenitor.Ui;

namespace Progenitor.SampleServerLed;

/// <summary>
/// Binds one cfg entry. <c>Plugin</c> binds through <c>ctx.Bind</c> (with real
/// <c>AcceptableValue*</c> bounds and explicit ServerWide scope); tests use the config-file shim.
/// </summary>
internal interface IEntryBinder
{
    ConfigEntry<int> Int(string section, string key, int value, string description, bool synced, int min, int max);

    ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null);

    ConfigEntry<bool> Flag(string section, string key, bool value, string description);
}

/// <summary>
/// The sample's shared server options and local popup options. Personal server preferences are
/// registered through ctx.Settings.PerPlayer in Plugin.cs and never bound in the client cfg. The server binds only
/// the server options; the client binds all of them (the server options synced, so the server's values
/// arrive through ConfigSync and an administrator can change them from the page).
/// </summary>
internal sealed class ServerLedEntries
{
    internal const string ServerSection = "Server";
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

    /// <summary>Client option for the client: seconds before a popup closes itself (0 = until OK). Client only.</summary>
    internal ConfigEntry<int>? PopupSeconds { get; private set; }

    /// <summary>Client option for the client: also write each announcement to the client log. Client only.</summary>
    internal ConfigEntry<bool>? LogAnnouncements { get; private set; }

    /// <summary>Client option for the client: log whether each joined server runs the server part. Client only.</summary>
    internal ConfigEntry<bool>? JoinCheck { get; private set; }

    /// <summary>Bind this side's entries. Bind before <c>ctx.Settings.Page</c> (the real binder declares ServerWide at bind time).</summary>
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
/// The sample's cfg page: server-wide and local popup sections. Core adds personal server
/// controls from the manifest; do not add duplicate cfg-backed rows for them. Game-free tests
/// build this page and read its scopes and grayed rows.
/// </summary>
internal static class ServerLedSettings
{
    internal const string SectionServer = "Server options (for the server)";
    internal const string SectionForClient = "Client options for the client";

    internal const string FallbackServer =
        "This server does not run the server part, so there is nothing to set here.";

    internal const string FallbackPopup =
        "Announcements come only from the server part, so nothing is shown.";

    /// <summary>
    /// Lay out the page. On the server only <see cref="SectionServer"/> is built: the server has no
    /// page to draw, and the binder already declares its entries ServerWide.
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
                needsServerPart: true, fallback: FallbackServer, scope: SettingScope.ServerWide)
            .Text(entries.Greeting,
                help: "What every announcement says first. Stored in the server's cfg; an administrator can change it here while joined.",
                needsServerPart: true, fallback: FallbackServer, scope: SettingScope.ServerWide);

        if (entries.PopupSeconds == null
            || entries.LogAnnouncements == null || entries.JoinCheck == null)
            return;

        page.Section(SectionForClient)
            .Slider(entries.PopupSeconds, ServerLedPolicy.MinPopupSeconds, ServerLedPolicy.MaxPopupSeconds, 1, "s",
                help: "How long an announcement popup stays; 0 keeps it until you press OK. Used by this client only.",
                needsServerPart: true, fallback: FallbackPopup, scope: SettingScope.Personal)
            .Toggle(entries.LogAnnouncements,
                help: "Write each announcement to this client's log too.",
                needsServerPart: true, fallback: FallbackPopup, scope: SettingScope.Personal)
            .Toggle(entries.JoinCheck,
                help: "On each join, log whether the server runs the server part. Works on any server.",
                scope: SettingScope.Personal);

        if (serverPart != null)
            page.Info("Server part", serverPart, 1,
                help: "What this client knows from the joined server's handshake.");
    }
}
