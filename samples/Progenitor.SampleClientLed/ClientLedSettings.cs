using System;
using BepInEx.Configuration;
using Progenitor.Ui;

namespace Progenitor.SampleClientLed;

/// <summary>
/// Binds one cfg entry. <c>Plugin</c> binds through <c>ctx.Bind</c> (with a real
/// <c>AcceptableValueList</c> and <c>synced</c>); the tests bind through the config-file shim.
/// </summary>
internal interface IEntryBinder
{
    ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null);

    ConfigEntry<bool> Flag(string section, string key, bool value, string description);
}

/// <summary>
/// The client-led sample's cfg entries. The server part binds only the server default (synced, so a
/// client receives it and an administrator can change it); the client binds all of them.
/// </summary>
internal sealed class ClientLedEntries
{
    internal const string ServerSection = "Server";
    internal const string ClockSection = "Clock";

    private ClientLedEntries(ConfigEntry<string> serverDefaultFormat) => ServerDefaultFormat = serverDefaultFormat;

    /// <summary>Server-set: the clock format a client's <c>Server default</c> means (server cfg; synced; admin-editable).</summary>
    internal ConfigEntry<string> ServerDefaultFormat { get; }

    /// <summary>Local: <c>Server default</c>, <c>24-hour</c> or <c>12-hour</c> (overrides the server's default). Client only.</summary>
    internal ConfigEntry<string>? Format { get; private set; }

    /// <summary>Local: show seconds in the local time. Client only.</summary>
    internal ConfigEntry<bool>? ShowSeconds { get; private set; }

    /// <summary>Needs the server part: include the server's in-game time in the clock. Client only.</summary>
    internal ConfigEntry<bool>? ShowServerTime { get; private set; }

    /// <summary>Bind this side's entries. Bind before <c>ctx.Settings.Page</c> (a synced entry is opted into admin edits there).</summary>
    internal static ClientLedEntries Bind(IEntryBinder bind, bool isClient)
    {
        if (bind == null) throw new ArgumentNullException(nameof(bind));
        var entries = new ClientLedEntries(bind.Text(ServerSection, "DefaultFormat", ClientLedPolicy.Format24,
            "The clock format a client set to Server default uses on this server. Set on the server.",
            synced: true, ClientLedPolicy.ServerFormats));
        if (!isClient) return entries;

        entries.Format = bind.Text(ClockSection, "Format", ClientLedPolicy.FormatServerDefault,
            "Server default, 24-hour or 12-hour. Your own choice overrides the server's default.",
            synced: false, ClientLedPolicy.ClientFormats);
        entries.ShowSeconds = bind.Flag(ClockSection, "ShowSeconds", true, "Show seconds in your local time.");
        entries.ShowServerTime = bind.Flag(ClockSection, "ShowServerTime", true,
            "Show the server's in-game time and next sunrise or sunset (needs the server part).");
        return entries;
    }
}

/// <summary>
/// The client-led sample's mod page (<c>ctx.Settings.Page</c>). Game-free: the tests build the same
/// page and read its scopes and grayed rows.
/// </summary>
internal static class ClientLedSettings
{
    internal const string SectionClock = "Clock (works on any server)";
    internal const string SectionServer = "With the server part";

    /// <summary>The keybind id; Ctrl+F6 by default.</summary>
    internal const string KeybindId = "ShowClock";

    internal const string FallbackServerTime = "Uses your local estimate: the clock shows your local time only.";

    internal const string FallbackServerDefault = "Without the server part, Server default means 24-hour.";

    /// <summary>
    /// Lay out the page. On the server only the server default is placed: the server has no page to
    /// draw, and the call is what opts the synced entry into administrator edits.
    /// </summary>
    internal static void Build(
        SettingsPageBuilder page, ClientLedEntries entries, Func<string>? localTime, Func<string>? serverTime, Func<string>? serverPart = null)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        if (entries.Format == null || entries.ShowSeconds == null || entries.ShowServerTime == null)
        {
            page.Section(SectionServer)
                .Radio(entries.ServerDefaultFormat, ClientLedPolicy.ServerFormats,
                    needsServerPart: true, fallback: FallbackServerDefault, scope: SettingScope.Server);
            return;
        }

        page.Section(SectionClock)
            .Radio(entries.Format, ClientLedPolicy.ClientFormats,
                help: "Server default follows the server part's setting below while it runs, and is 24-hour otherwise.",
                scope: SettingScope.Local)
            .Toggle(entries.ShowSeconds, help: "Show seconds in your local time.", scope: SettingScope.Local)
            .Keybind(KeybindId, label: "Show the clock", help: "Opens a small popup with the time.");
        if (localTime != null) page.Info("Local time", localTime, 1);

        page.Section(SectionServer)
            .Toggle(entries.ShowServerTime,
                help: "Add the server's in-game time and the next sunrise or sunset to the clock.",
                needsServerPart: true, fallback: FallbackServerTime, scope: SettingScope.Local)
            .Radio(entries.ServerDefaultFormat, ClientLedPolicy.ServerFormats,
                help: "What Server default means on this server. Stored in the server's cfg; an administrator can change it here while joined.",
                needsServerPart: true, fallback: FallbackServerDefault, scope: SettingScope.Server);
        if (serverPart != null)
            page.Info("Server part", serverPart, 1, help: "What this client knows from the joined server's handshake.");
        if (serverTime != null)
            page.Info("In-game time", serverTime, 1,
                help: "Estimated from the server part's last answer; the fallback line while there is none.");
    }
}
