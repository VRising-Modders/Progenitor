using System;
using BepInEx;
using Progenitor.Plugins;

namespace Progenitor.UpdateProbe;

/// <summary>
/// AUTOUPDATE2: the auto-update live tests' package. Its version is the build's (<c>-p:ProgenitorVersion=1.1.0</c>), so
/// each published version is a different image with a matching <c>[BepInPlugin]</c> version, which admission requires.
/// Client only, hot-loadable (no Harmony, no injected types), one log line each way. <c>-p:UpdateProbeFailLoad=true</c>
/// builds a copy whose <c>OnLoad</c> throws, the "failed to load" case the automatic rollback handles.
/// AUTOUPDATE3: <c>-p:UpdateProbeVariant=Server</c> builds a server-only probe (<c>progenitor.updateprobe.server</c>) and
/// <c>-p:UpdateProbeVariant=Both</c> one both sides run that the server's handshake requires
/// (<c>progenitor.updateprobe.both</c>, ServerMustHave, Exact), for the server's update tests.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
#if UPDATEPROBE_SERVER
[ProgenitorSide(Side.Server)]
#elif UPDATEPROBE_BOTH
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
#else
[ProgenitorSide(Side.Client)]
#endif
public sealed class Plugin : ProgenitorPlugin<UpdateProbeContext>
{
    protected override void OnLoad(UpdateProbeContext ctx)
    {
#if UPDATEPROBE_FAIL
        throw new InvalidOperationException("Update probe " + MyPluginInfo.PLUGIN_VERSION + ": this build fails at load on purpose (auto-update rollback test).");
#else
        ctx.Log.LogInfo("Update probe " + MyPluginInfo.PLUGIN_VERSION + ": loaded");
#endif
    }

    protected override void OnUnload(UpdateProbeContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("Update probe " + MyPluginInfo.PLUGIN_VERSION + ": unloading (" + reason + ")");
}

public sealed class UpdateProbeContext : PluginContext { }
