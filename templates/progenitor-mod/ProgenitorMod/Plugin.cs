// SPDX-License-Identifier: MIT
using BepInEx;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace ProgenitorMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
// Both: one DLL with a client part and a server part. Client-only or server-only mod: Side.Client / Side.Server.
[ProgenitorSide(Side.Both)]
// The two parts talk to each other, so their versions must agree. Keep Peers in step with your wire changes
// (new field: minor; removed or retyped: major). Client-only mod: NetworkCompatibilityLevel.ClientOnly, no Peers.
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, Peers = ">=1.0.0 <2.0.0")]
// Limited: the client part still runs on a server without the server part; the listed features are grayed.
// Required: nothing works without it (the join is refused). Optional: the other part only adds extras.
[Counterpart(CounterpartNeed.Limited, Features = new[] { "Server greeting" })]
// Shown on the Mods page only when no README.md is embedded (this project embeds ../README.md).
[ModAbout("Asks the server for its greeting. Open the window with **Ctrl+F8**.")]
public sealed class Plugin : ProgenitorPlugin<ProgenitorModContext>
{
    /// <summary>The keybind id. It names the cfg key, so keep it after the first release.</summary>
    internal const string ToggleWindowBind = "ToggleWindow";

    protected override void OnLoad(ProgenitorModContext ctx)
    {
        var settings = new ModSettings(ctx);
        // Both sides: the server needs the page call to accept admin edits of the server-wide row.
        ctx.Settings.Page(settings.Build);

        if (ctx.IsServer)
        {
            ctx.Requests.Handle<GreetingRequest, GreetingReply>(
                (user, request, info) => ModRules.Answer(request, settings.Greeting.Value, ctx.Version),
                new RequestOptions { MaxRequestBytes = 256, Burst = 5, PerSecond = 1 });
        }
        else
        {
            var window = new ModWindow(ctx, settings);
            ctx.Track(window);
            ctx.Keybinds.RegisterOpener(ToggleWindowBind, new KeyChord(UiKey.F8, KeyModifiers.Ctrl),
                "Open or close the " + MyPluginInfo.PLUGIN_NAME + " window.", window.Toggle);
        }

        ctx.Log.LogInfo(ModRules.Notice(MyPluginInfo.PLUGIN_NAME, "loaded " + MyPluginInfo.PLUGIN_VERSION + (ctx.IsServer ? " (server)" : " (client)")));
    }

    // Everything registered through ctx is released on every unload reason. Release anything else here.
    protected override void OnUnload(ProgenitorModContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo(ModRules.Notice(MyPluginInfo.PLUGIN_NAME, "unloading, reason=" + reason));
}

/// <summary>Keep the context empty: state lives in tracked objects, so a hot reload starts clean.</summary>
public sealed class ProgenitorModContext : PluginContext { }

/// <summary>The mod's settings and their page.</summary>
internal sealed class ModSettings
{
    /// <summary>Personal: each player decides.</summary>
    internal readonly ConfigEntry<bool> ShowNotices;

    /// <summary>Server-wide: the server's value applies to everyone; admins edit it in game.</summary>
    internal readonly ConfigEntry<string> Greeting;

    internal ModSettings(PluginContext ctx)
    {
        ShowNotices = ctx.Bind("Display", "ShowNotices", true, "Show this mod's notices at the top of the screen.");
        Greeting = ctx.Bind("Server", "Greeting", ModRules.DefaultGreeting, "What the server answers when a player asks.", synced: true);
    }

    internal void Build(SettingsPageBuilder page) => page
        .Section("Display")
            .Toggle(ShowNotices, label: "Show notices")
            .Keybind(Plugin.ToggleWindowBind, label: "Open the window")
        .Section("Server")
            .Text(Greeting, label: "Greeting", scope: SettingScope.ServerWide, needsServerPart: true,
                fallback: "Not on this server").MaxLength(ModRules.MaxGreetingLength);
}
