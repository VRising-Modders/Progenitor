using BepInEx;
using Progenitor.Commands;
using Progenitor.Events;
using Progenitor.Plugins;

namespace Progenitor.ReloadExample;

[BepInPlugin("progenitor.reloadexample", "Progenitor Reload Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Server)]
public sealed class Plugin : ProgenitorPlugin<ReloadContext>
{
    protected override void OnLoad(ReloadContext ctx)
    {
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

public sealed class ReloadContext : PluginContext { }

public static class ReloadCommands
{
    [Command("reloadhello", description: "Show the currently loaded reload-example message.")]
    public static void Hello(ICommandContext ctx) => ctx.Reply("Reload example v1");
}
