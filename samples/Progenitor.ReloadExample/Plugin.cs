using BepInEx;
using Progenitor.Commands;
using Progenitor.Events;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.Players;
using BepInEx.Configuration;

namespace Progenitor.ReloadExample;

[BepInPlugin("progenitor.reloadexample", "Progenitor Reload Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Server)]
public sealed class Plugin : ProgenitorPlugin<ReloadContext>
{
    protected override void OnLoad(ReloadContext ctx)
    {
        ctx.ReceiveHints = ctx.Settings.PerPlayer("Hints", "Receive hints", true, "Show hints just for you on this server.");
        ctx.Detail = ctx.Settings.PerPlayer("Hints", "Detail", "Brief", "Your hint detail on this server.",
            new AcceptableValueList<string>("Brief", "Detailed"));
        ctx.Bind("Rules", "Hint interval", 10, scope: SettingScope.ServerWide,
            description: "How often hints may appear for everyone on this server.", acceptableValues: new AcceptableValueRange<int>(1, 120));
        ctx.Commands.Register(typeof(ReloadCommands));
        ctx.Events.On<PlayerConnected>(e => ctx.Log.LogInfo("Reload example: joined " + e.CharacterName));
        ctx.Log.LogInfo("Reload example: loaded");
    }

    protected override void OnUnload(ReloadContext ctx, PluginUnloadReason reason)
    {
        ctx.Log.LogInfo("Reload example: unloading, reason=" + reason);
        // The context removes our command and event subscription on every unload.
    }
}

public sealed class ReloadContext : PluginContext
{
    public PlayerSetting<bool> ReceiveHints { get; set; } = null!;
    public PlayerSetting<string> Detail { get; set; } = null!;
}

public static class ReloadCommands
{
    [Command("reloadhello", description: "Show the currently loaded reload-example message.")]
    public static void Hello(ICommandContext ctx) => ctx.Reply("Reload example v1");
}
