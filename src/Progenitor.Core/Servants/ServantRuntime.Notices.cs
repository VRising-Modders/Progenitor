using System;
using System.Collections.Generic;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// <c>ctx.Servants.SuppressGameNotices</c>: drop the game's hunt chat lines for chosen users.
///
/// <para>The lines are not chat messages: the server raises a network event to each recipient and
/// the client prints the System line from it (<c>ServantMissionSystem_Client</c>, which does nothing
/// else with the event). The game's throne send raises <c>ServantMissionStartedEvent</c>, and its
/// return raises <c>ServantMissionFinishedEvent</c> (servant name, mission, <c>Success</c>) once per
/// servant, both to every user <c>TeamUtility.GetAlliedUsers</c> returns, one event entity per user
/// (<c>NetworkEvents.SendEvent(commandBuffer, event, in User)</c>: <c>NetworkEventType</c>, the
/// event, <c>SendNetworkEventTag</c>, <c>SendEventToUser{UserIndex}</c>) through the
/// <c>PostUpdateBarrier</c> command buffer. That buffer plays back after <c>UpdateGroup</c> and
/// before <c>BeforeSerializeBarrierGroup</c>, whose <c>SerializeAndSendServerEventsSystem</c> sends
/// every event entity and destroys it. So a Before hook on that system sees each recipient's own
/// event and destroying it drops the line for that user only; nothing has to be sent again.</para>
///
/// <para>Progenitor's own "hunt started" notice is one entity with a user mask; it is filtered where
/// it is built (<see cref="AnnounceStarted"/>), so this hook reads single-recipient events only.</para>
/// </summary>
internal static unsafe partial class ServantRuntime
{
    internal static readonly ServantNoticeFilters<Entity> Notices = new();

    private static IDisposable? _sendBefore;

    private static void InstallNotices()
    {
        Notices.Log = line => Plugin.Logger?.LogInfo(line);
        _sendBefore = SystemHooks.Before<SerializeAndSendServerEventsSystem>(BeforeServerSend);
    }

    private static void UninstallNotices()
    {
        try { _sendBefore?.Dispose(); } catch { }
        _sendBefore = null;
        Notices.Clear();
        Notices.Log = null;
    }

    private static void BeforeServerSend(SystemHookContext hook)
    {
        if (!Notices.Any) return;
        var em = hook.World.EntityManager;
        Dictionary<int, Entity>? users = null;
        List<Entity>? drop = null;
        try
        {
            ScanNotices(em, finished: true, ref users, ref drop);
            ScanNotices(em, finished: false, ref users, ref drop);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: reading the hunt notices failed; the game's lines are sent: " + e.Message);
            return;
        }
        if (drop == null) return;
        int dropped = 0;
        foreach (var ev in drop)
        {
            try
            {
                if (!em.Exists(ev)) continue;
                em.DestroyEntity(ev);
                dropped++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("servants: dropping a hunt notice failed; that line is sent: " + e.Message);
            }
        }
        if (dropped > 0) Plugin.Logger?.LogInfo("servants: dropped " + dropped + " game hunt notice(s) by filter");
    }

    private static void ScanNotices(EntityManager em, bool finished, ref Dictionary<int, Entity>? users, ref List<Entity>? drop)
    {
        var events = finished
            ? Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ServantMissionFinishedEvent>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<SendEventToUser>(ComponentType.AccessMode.ReadOnly))
            : Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ServantMissionStartedEvent>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<SendEventToUser>(ComponentType.AccessMode.ReadOnly));
        if (events.Count == 0) return;
        users ??= ConnectedUsersByIndex(em);
        foreach (var ev in events)
        {
            byte* to = AiRuntime.RO<SendEventToUser>(em, ev, Layout.SendEventToUserUserIndexOffset + 4);
            if (to == null) continue;
            int userIndex = *(int*)(to + Layout.SendEventToUserUserIndexOffset);
            if (!users.TryGetValue(userIndex, out var user)) continue; // nobody connected there: the game drops it anyway
            var kind = ServantNoticeKind.Started;
            if (finished)
            {
                byte* p = AiRuntime.RO<ServantMissionFinishedEvent>(em, ev, Layout.ServantMissionFinishedEventSuccessOffset + 1);
                if (p == null) continue;
                kind = ServantNoticeFilters<Entity>.FinishedKind(p[Layout.ServantMissionFinishedEventSuccessOffset] != 0);
            }
            if (!Notices.ShouldSuppress(user, kind)) continue;
            (drop ??= new List<Entity>()).Add(ev);
        }
    }

    /// <summary>Connected user entities by <c>User.Index</c>, the index the game's per-user send writes.</summary>
    private static Dictionary<int, Entity> ConnectedUsersByIndex(EntityManager em)
    {
        var map = new Dictionary<int, Entity>();
        foreach (var u in Query.Entities(em, includeDisabled: false, includeSystems: false,
                     ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            var user = em.GetComponentData<User>(u);
            if (!user.IsConnected) continue;
            map[user.Index] = u;
        }
        return map;
    }
}
