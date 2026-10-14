using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Progenitor.PanelsExample;

/// <summary>Client to server: the network id words of the container window's target.</summary>
[ModMessage("panels.resolve")]
internal sealed class ResolveTargetRequest
{
    [Field(1)] public int Index;
    [Field(2)] public int Generation;
    [Field(3)] public int Type;
}

/// <summary>
/// The server's answer: the stable container id and the network id words read back from that id
/// (what a server would send to locate a container it only knows by <c>ContainerId</c>).
/// </summary>
[ModMessage("panels.resolved")]
internal sealed class ResolveTargetReply
{
    [Field(1)] public bool Ok;
    [Field(2)] public string Id = "";
    [Field(3)] public string Owner = "";
    [Field(4)] public int Index;
    [Field(5)] public int Generation;
    [Field(6)] public int Type;
    [Field(7)] public string Reason = "";
}

/// <summary>
/// The request-then-resolve pattern for a vanilla panel target: the client sends
/// <c>VanillaPanelTarget</c>'s network id words, the server resolves them with
/// <c>ctx.Inventory.TryResolveNetworkId</c> to a <c>ContainerId</c>, turns that id back into
/// words with <c>ctx.Inventory.TryGetNetworkId</c>, and the client finds its own entity with
/// <c>ctx.Hud.TryFindByNetworkId</c> and pings it with <c>ctx.Hud.Locate</c>.
/// </summary>
internal static class Locate
{
    /// <summary>Server: the handler. Every word is untrusted; the resolver validates them.</summary>
    internal static void AttachServer(PanelsContext ctx)
    {
        ctx.Requests.Handle<ResolveTargetRequest, ResolveTargetReply>((user, request, info) =>
        {
            var reply = new ResolveTargetReply();
            if (!ctx.Inventory.TryResolveNetworkId(request.Index, request.Generation, request.Type, out var id, out var owner, out string reason))
            {
                reply.Reason = reason;
                ctx.Log.LogInfo("Panels example: resolve net=" + request.Index + ":" + request.Generation + " type=" + request.Type + " refused: " + reason);
                return reply;
            }

            reply.Id = id.ToString();
            reply.Owner = owner.Index + ":" + owner.Version;
            if (!ctx.Inventory.TryGetNetworkId(id, out int index, out int generation, out int type, out reason))
            {
                reply.Reason = reason;
                ctx.Log.LogInfo("Panels example: resolve " + id + " has no network id back: " + reason);
                return reply;
            }

            reply.Ok = true;
            reply.Index = index;
            reply.Generation = generation;
            reply.Type = type;
            ctx.Log.LogInfo("Panels example: resolve net=" + request.Index + ":" + request.Generation + " -> entity=" + reply.Owner
                + " id=" + id + " back=" + index + ":" + generation + " type=" + type);
            return reply;
        }, new RequestOptions { MaxRequestBytes = 64, Burst = 4, PerSecond = 1 });
    }

    /// <summary>Client: send the panel's target, then locate what the server answered.</summary>
    internal static void Send(PanelsContext ctx, VanillaPanelTarget target)
    {
        if (!target.HasNetworkId)
        {
            ctx.Log.LogInfo("Panels example: locate: the target has no network id (" + target + ")");
            return;
        }

        var request = new ResolveTargetRequest { Index = target.NetworkIdIndex, Generation = target.NetworkIdGeneration, Type = target.NetworkId2 };
        ctx.Requests.Send<ResolveTargetRequest, ResolveTargetReply>(request, r =>
        {
            if (!r.Ok || r.Reply == null)
            {
                ctx.Log.LogInfo("Panels example: locate: request " + r.Status + " " + r.Reason);
                return;
            }

            var reply = r.Reply;
            if (!reply.Ok)
            {
                ctx.Log.LogInfo("Panels example: locate: server refused: " + reply.Reason);
                return;
            }

            if (!ctx.Hud.TryFindByNetworkId(reply.Index, reply.Generation, reply.Type, out var entity, out string why))
            {
                ctx.Log.LogInfo("Panels example: locate: " + reply.Id + " not found on this client: " + why);
                return;
            }

            ctx.Hud.Locate(entity, "Chest", 10);
            ctx.Log.LogInfo("Panels example: locate " + reply.Id + " server=" + reply.Owner + " client=" + entity.Index + ":" + entity.Version
                + " same=" + (entity.Index == target.EntityIndex && entity.Version == target.EntityVersion));
        });
    }
}
