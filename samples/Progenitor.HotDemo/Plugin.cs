using System;
using BepInEx;
using Progenitor.Commands;
using Progenitor.Plugins;

namespace Progenitor.HotDemo;

/// <summary>
/// Job HOT2: the smallest plugin that can be hot loaded into a running <b>client</b>, so the Install
/// tab's apply-now lane can be exercised end to end without a restart.
///
/// <para><b>Why a second demo sample.</b> <c>Progenitor.ReloadExample</c> is
/// <c>[ProgenitorSide(Side.Server)]</c>, and a server-side package is not admitted for a client
/// target — the admission evaluator refuses it with <c>SideMismatch</c>, correctly. Declaring
/// <c>Side.Both</c> on a plugin that really does load on both sides is the honest fix; widening the
/// existing sample's declaration would have been a lie about what its code does (it registers a
/// <c>PlayerConnected</c> handler and a server command).</para>
///
/// <para><b>What it deliberately does not do.</b> No Harmony, no <c>ctx.UnsafeHarmony</c>, no
/// <c>ClassInjector</c>, no <c>DllImport</c>, no reflection and no dynamic loading — every one of
/// those is a finding the offline artifact preflight refuses or calls Unknown, so a demo that used
/// one could never reach apply-now. One log line each way and one owned command is the whole
/// surface, and both are released by the context on every unload reason.</para>
/// </summary>
#if HOTDEMO_REQUIRED
// JOINFLOW4 test build (-p:HotDemoRequired=true): a required, hot-swappable mod for the "keep both versions" live
// recipe. Both sides must run it at exactly the same version, and the version is the build's (-p:ProgenitorVersion=…
// makes an "ahead" client copy). The default build keeps 0.1.0 and no handshake row, as the Install-tab recipes need.
[BepInPlugin("progenitor.hotdemo", "Progenitor Hot Demo", MyPluginInfo.PLUGIN_VERSION)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
#else
[BepInPlugin("progenitor.hotdemo", "Progenitor Hot Demo", "0.1.0")]
#endif
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
public sealed class Plugin : ProgenitorPlugin<HotDemoContext>
{
    protected override void OnLoad(HotDemoContext ctx)
    {
#if HOTDEMO_REQUIRED
        ctx.Handshake.DeclareSelf();
#endif
        HotDemoGeneration.Next();
        ctx.Commands.Register(typeof(HotDemoCommands));
        ctx.Log.LogInfo("Hot demo: loaded, gen=" + HotDemoGeneration.Current);
    }

    protected override void OnUnload(HotDemoContext ctx, PluginUnloadReason reason)
    {
        // The context takes the command registration back on every reason; nothing else is owned.
        ctx.Log.LogInfo("Hot demo: unloading, gen=" + HotDemoGeneration.Current + ", reason=" + reason);
    }
}

public sealed class HotDemoContext : PluginContext { }

/// <summary>
/// How many times this <em>process</em> has loaded the demo. The point of the number is to make a
/// real hot load visible: a fresh process says <c>gen=1</c>, and a hot swap that actually happened
/// says <c>gen=2</c> without the client ever restarting.
///
/// <para>It cannot be an ordinary static. This assembly is loaded into a collectible
/// <c>AssemblyLoadContext</c> and a new one is minted for every load, so a static field here starts
/// at zero each time and could never tell a reload from a first start. <c>AppDomain</c> data is the
/// one process-lifetime slot reachable without a framework seam, and what it stores is a boxed
/// <see cref="int"/> — a <c>System.Private.CoreLib</c> type — so it pins nothing of this plugin's
/// ALC. Storing anything of ours here (a delegate, an instance, a <c>Type</c>) would leak the
/// generation for the life of the process, which is the exact failure the hot-reload rules exist to
/// prevent.</para>
/// </summary>
internal static class HotDemoGeneration
{
    private const string Key = "progenitor.hotdemo.generation";

    /// <summary>The count so far, or 0 before the first load in this process.</summary>
    public static int Current
    {
        get
        {
            try { return AppDomain.CurrentDomain.GetData(Key) is int n ? n : 0; }
            catch { return 0; }
        }
    }

    /// <summary>Count this load. Never throws: a demo counter must not fail a load.</summary>
    public static void Next()
    {
        try { AppDomain.CurrentDomain.SetData(Key, Current + 1); }
        catch { /* the reply then says gen=0, which is honest about not knowing */ }
    }
}

public static class HotDemoCommands
{
    /// <summary>
    /// <c>.hotdemo</c> — the liveness check the hot-load acceptance reads. It answers only while this
    /// plugin is loaded, so it distinguishes "the image was written" from "the plugin is running".
    /// </summary>
    [Command("hotdemo", description: "Reply that the hot demo is loaded, with this process's load generation.")]
    public static void Alive(ICommandContext ctx) =>
        ctx.Reply("hotdemo alive gen=" + HotDemoGeneration.Current);
}
