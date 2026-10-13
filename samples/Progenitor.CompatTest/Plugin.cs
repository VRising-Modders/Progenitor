using System;
using BepInEx;
using Progenitor.Networking;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.CompatTest;

#if !COMPATTEST_OTHER && !COMPATTEST_RANGE && !COMPATTEST_DEFAULT
/// <summary>
/// TEST PLUGIN (mod compatibility C1/C2 live tests; docs/mod-compatibility.md). Both sides run it, at different versions:
/// 1.0.0 offers <c>hello</c> at level 1 and writes <see cref="Hello"/> version 1; the <c>-p:CompatTestV2=true</c> build
/// (1.1.0) offers <c>hello</c> at level 2 plus <c>notes</c>, and adds field 3 to <see cref="Hello"/>. The client says
/// hello whenever the server's table arrives or changes; the server answers each hello and greets every client whose
/// table lists this mod. Every observation is one <c>CompatTest:</c> log line. It also sends one raw <c>ctx.Net</c>
/// message per connection so the unversioned-message warning shows. No keys, no Harmony: hot-loadable.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None)]
public sealed class Plugin : ProgenitorPlugin<CompatTestContext>
{
    /// <summary><see cref="Hello"/>'s version in this build (CompatTestMessages.cs).</summary>
    internal const int HelloVersion = CompatTestMessages.HelloVersion;

    /// <summary><c>'CtRw'</c>: the raw message that shows the unversioned-message warning. Both sides handle it.</summary>
    internal const int RawEventId = 0x43745277;

    internal const string OtherGuid = "progenitor.compattest.other";

    protected override unsafe void OnLoad(CompatTestContext ctx)
    {
        string me = "CompatTest " + ctx.Version + " (" + ctx.Side + ")";
        ctx.Features.Offer("hello", HelloVersion);
#if COMPATTEST_V2
        ctx.Features.Offer("notes");
#endif
        ctx.Log.LogInfo("CompatTest: " + me + " offers " + Describe(ctx.Features.Offered) + "; Hello is message version " + HelloVersion);

        ctx.Messages.On<Hello>((from, msg, info) =>
        {
            ctx.Log.LogInfo("CompatTest: " + me + " received hello seq=" + msg.Seq + " text='" + msg.Text + "'" + NoteOf(msg)
                            + " (sender wrote v" + info.SenderVersion + ", sender mod " + (info.SenderModVersion ?? "?") + ")");
            if (ctx.IsServer && msg.Seq < 1000)
            {
                var reply = NewHello(msg.Seq + 1000, "echo from server " + ctx.Version);
                ctx.Log.LogInfo("CompatTest: " + me + " echo to user " + info.UserIndex + ": " + ctx.Messages.SendTo(from, reply));
            }
        });

        if (ctx.IsServer)
        {
            ctx.Net.OnReceiveFromClient(RawEventId, (id, user, u, payload, length) =>
                ctx.Log.LogInfo("CompatTest: " + me + " received the raw ctx.Net message (" + length + " bytes)"));
            ctx.Peers.Changed += user =>
            {
                var p = ctx.Peers.Of(user);
                ctx.Log.LogInfo("CompatTest: " + me + " peer changed: " + Describe(p));
                if (p.Has("hello"))
                    ctx.Log.LogInfo("CompatTest: " + me + " greet: " + ctx.Messages.SendTo(user, NewHello(500, "greeting from server " + ctx.Version)));
            };
        }
        else
        {
            ctx.Net.OnReceive(RawEventId, (id, payload, length) => { });
            ctx.Peer.Changed += () =>
            {
                ctx.Log.LogInfo("CompatTest: " + me + " peer changed: " + Describe(ctx.Peer));
                if (!ctx.Peer.Known) return;
                ctx.State.Seq++;
                ctx.Log.LogInfo("CompatTest: " + me + " hello to server: " + ctx.Messages.SendToServer(NewHello(ctx.State.Seq, "from client " + ctx.Version)));
                if (ctx.Peer.Has("hello") && !ctx.State.RawSent)
                {
                    ctx.State.RawSent = true;
                    int n = 1;
                    ctx.Log.LogInfo("CompatTest: " + me + " raw ctx.Net send: " + ctx.Net.SendToServer(RawEventId, n));
                }
            };
        }
    }

    protected override void OnUnload(CompatTestContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("CompatTest: CompatTest " + ctx.Version + " unloading (" + reason + ")");

    private static Hello NewHello(int seq, string text)
    {
        var h = new Hello { Seq = seq, Text = text };
#if COMPATTEST_V2
        h.Note = "note from " + MyPluginInfo.PLUGIN_VERSION;
#endif
        return h;
    }

    private static string NoteOf(Hello h)
    {
#if COMPATTEST_V2
        return " note='" + h.Note + "'";
#else
        return "";
#endif
    }

    internal static string Describe(System.Collections.Generic.IReadOnlyDictionary<string, int> offered)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in offered) parts.Add(kv.Key + "=" + kv.Value);
        parts.Sort(StringComparer.Ordinal);
        return "{" + string.Join(", ", parts) + "}";
    }

    internal static string Describe(IPeerView p) =>
        "known=" + p.Known + " core=" + (p.CoreVersion ?? "?") + " compattest=" + (p.ModVersion(MyPluginInfo.PLUGIN_GUID) ?? "absent")
        + " hello>=1=" + p.Has("hello") + " hello>=2=" + p.Has("hello", 2) + " notes=" + p.Has("notes")
        + " other.beacon=" + p.Has(OtherGuid, "beacon") + " other=" + (p.ModVersion(OtherGuid) ?? "absent");
}

public sealed class CompatTestState
{
    public int Seq;
    public bool RawSent;
}

public sealed class CompatTestContext : PluginContext
{
    public CompatTestState State { get; } = new();
}
#elif COMPATTEST_OTHER
/// <summary>
/// TEST PLUGIN, the second one (mod compatibility C1/C2 live tests). Run it on one side only: it offers <c>beacon</c>,
/// which the other test plugin's peer view reports as <c>other.beacon</c>, and on the server it tries to send its own
/// message to each client whose table arrives, which a client without this mod refuses at the sender
/// (<c>PeerLacksMod</c>).
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None)]
public sealed class OtherPlugin : ProgenitorPlugin<OtherContext>
{
    protected override void OnLoad(OtherContext ctx)
    {
        string me = "CompatTest.Other " + ctx.Version + " (" + ctx.Side + ")";
        ctx.Features.Offer("beacon");
        ctx.Messages.On<Ping>((from, msg, info) => ctx.Log.LogInfo("CompatTest: " + me + " received ping " + msg.N));
        if (ctx.IsServer)
            ctx.Peers.Changed += user =>
            {
                var p = ctx.Peers.Of(user);
                if (!p.Known) return;
                ctx.Log.LogInfo("CompatTest: " + me + " ping to a client that " + (p.ModVersion(ctx.Guid) == null ? "lacks" : "runs")
                                + " this mod: " + ctx.Messages.SendTo(user, new Ping { N = 1 }));
            };
        ctx.Log.LogInfo("CompatTest: " + me + " offers beacon");
    }

    protected override void OnUnload(OtherContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("CompatTest: CompatTest.Other " + ctx.Version + " unloading (" + reason + ")");
}

public sealed class OtherContext : PluginContext
{
}
#elif COMPATTEST_RANGE
/// <summary>
/// TEST PLUGIN (mod compatibility C3 live tests): a <c>ServerMustHave</c> mod that declares the peer versions it works
/// with, <c>Peers = "&gt;=1.2.0 &lt;2.0.0"</c>, built at several versions with <c>-p:ProgenitorVersion</c>. It binds the
/// synced setting <c>[Sync] Limit</c> (default 3); the <c>-p:CompatTestNewKey=true</c> build also binds
/// <c>[Sync] NewLimit</c> (default 5), which an older server does not send, so the client locks it to its default for
/// the session. Every observation is one <c>CompatTest:</c> log line. No keys, no Harmony: hot-loadable.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = ">=1.2.0 <2.0.0")]
public sealed class RangePlugin : ProgenitorPlugin<RangeContext>
{
    protected override void OnLoad(RangeContext ctx)
    {
        string me = "CompatTest.Range " + ctx.Version + " (" + ctx.Side + ")";
        var limit = ctx.Bind("Sync", "Limit", 3, "C3 test: a synced setting every version has.", synced: true);
#if COMPATTEST_NEWKEY
        BepInEx.Configuration.ConfigEntryBase? newLimit = ctx.Bind("Sync", "NewLimit", 5, "C3 test: a synced setting only the newer builds have.", synced: true);
#else
        BepInEx.Configuration.ConfigEntryBase? newLimit = null;
#endif
        string Values() => "Limit=" + limit.Value + (ConfigSync.IsLocked(limit) ? " (locked)" : "")
            + (newLimit == null ? "" : " NewLimit=" + newLimit.BoxedValue + (ConfigSync.IsLocked(newLimit) ? " (locked)" : ""));
        ctx.Log.LogInfo("CompatTest: " + me + " loaded; peers >=1.2.0 <2.0.0; " + Values());
        if (ctx.IsClient)
            ctx.Peer.Changed += () =>
                ctx.Log.LogInfo("CompatTest: " + me + " peer changed: known=" + ctx.Peer.Known + " server has "
                                + (ctx.Peer.ModVersion(ctx.Guid) ?? "absent") + "; " + Values());
    }

    protected override void OnUnload(RangeContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("CompatTest: CompatTest.Range " + ctx.Version + " unloading (" + reason + ")");
}

public sealed class RangeContext : PluginContext
{
}
#elif COMPATTEST_DEFAULT
/// <summary>
/// TEST PLUGIN (mod compatibility C3 live tests): a <c>ServerMustHave</c> mod that declares only its level, so its
/// range is the cautious default, the same minor version (1.4.x for a 1.4.0 build).
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave)]
public sealed class DefaultPlugin : ProgenitorPlugin<DefaultContext>
{
    protected override void OnLoad(DefaultContext ctx) =>
        ctx.Log.LogInfo("CompatTest: CompatTest.Default " + ctx.Version + " (" + ctx.Side + ") loaded; declares no range");

    protected override void OnUnload(DefaultContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("CompatTest: CompatTest.Default " + ctx.Version + " unloading (" + reason + ")");
}

public sealed class DefaultContext : PluginContext
{
}
#endif
