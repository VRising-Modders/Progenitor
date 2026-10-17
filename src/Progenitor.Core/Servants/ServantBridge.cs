using System;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Servants;

/// <summary>
/// The Dev bridge's <c>servants</c> op over <c>ctx.Servants</c>. Public only because Core grants
/// <c>Progenitor.Dev</c> no <c>InternalsVisibleTo</c>; not a plugin API (plugins use
/// <c>ctx.Servants</c>). Server only and main thread only: every member throws
/// <see cref="InvalidOperationException"/> elsewhere, like <c>ctx.Servants</c>.
/// </summary>
public static class ServantBridge
{
    /// <summary>Who the bridge's sends are logged and published as (<see cref="ServantsSent.By"/>).</summary>
    public const string By = "progenitor.dev bridge";

    /// <summary>The castle heart of a heart, throne or anything connected to one; none when it is not.</summary>
    public static CastleId HeartOf(CastleId entity)
    {
        var world = ServantRuntime.RequireWorld("bridge");
        return CastleId.From(ServantRuntime.HeartOf(world.EntityManager, entity.ToEntity()));
    }

    /// <summary><c>ctx.Servants.List</c>.</summary>
    public static IReadOnlyList<ServantInfo> List(CastleId heart) => ServantRuntime.List(ServantRuntime.RequireWorld("List"), heart.ToEntity());

    /// <summary><c>ctx.Servants.Missions</c>.</summary>
    public static IReadOnlyList<ServantMission> Missions(CastleId heart) => ServantRuntime.Missions(ServantRuntime.RequireWorld("Missions"), heart.ToEntity());

    /// <summary><c>ctx.Servants.MissionOf</c>.</summary>
    public static int MissionOf(MissionZone zone) => ServantRuntime.MissionFor(ServantRuntime.RequireWorld("MissionOf").EntityManager, zone);

    /// <summary>
    /// The zones with a servant mission for this castle: discovered by <paramref name="user"/>
    /// (none: the heart's owner) unless <paramref name="all"/>, with the castle's active hunts, the
    /// mission's slots and the game's hunt length choices.
    /// </summary>
    public static ServantZonesReport Zones(CastleId heart, CastleId user, bool all)
    {
        var world = ServantRuntime.RequireWorld("Zones");
        var h = ServantRuntime.HeartOf(world.EntityManager, heart.ToEntity());
        if (h == Unity.Entities.Entity.Null) throw new InvalidOperationException("entity " + heart + " is not a castle heart or connected to one");
        return ServantRuntime.ZonesReport(world, h, user, all);
    }

    /// <summary>
    /// The zone a send picks when none is named: the first discovered zone with no hunt of this
    /// castle and at least <paramref name="servantCount"/> servant slots. Null with
    /// <paramref name="why"/> when none.
    /// </summary>
    public static ServantZoneInfo? PickZone(ServantZonesReport report, int servantCount, out string why)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        var z = ServantLogic.PickZone(report.Zones, servantCount);
        why = z == null ? ServantLogic.NoZoneReason(report.Zones, servantCount) : "";
        return z;
    }

    /// <summary><c>ctx.Servants.Send</c>, logged and published as <see cref="By"/>.</summary>
    public static ServantSendResult Send(ServantSendRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return ServantRuntime.Send(ServantRuntime.RequireWorld("Send"), request, By);
    }

    /// <summary><c>ctx.Servants.CapStatus</c>.</summary>
    public static ServantCapStatus CapStatus()
    {
        ServantRuntime.RequireWorld("CapStatus");
        return ServantRuntime.CapStatus();
    }

    /// <summary><c>ctx.Servants.Chance</c>: a prospective send's chances, no writes.</summary>
    public static ServantChanceResult Chance(ServantChanceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return ServantRuntime.Chance(ServantRuntime.RequireWorld("Chance"), request, By);
    }

    /// <summary>
    /// Make the castle's active hunts (or the one on <paramref name="missionGuid"/>, 0 for all) end
    /// now; the game returns them on its next update and rolls each servant's success there, as
    /// for any hunt. <paramref name="viaTimers"/> true: the game's servant-timers debug setting is
    /// turned on until those hunts are gone and then off again (server-wide: every hunt on the
    /// server returns). False: only the matching rows' start time is moved back.
    /// </summary>
    public static ServantFinishResult Finish(CastleId heart, int missionGuid, bool viaTimers)
    {
        var world = ServantRuntime.RequireWorld("Finish");
        var h = ServantRuntime.HeartOf(world.EntityManager, heart.ToEntity());
        if (h == Unity.Entities.Entity.Null)
            return new ServantFinishResult { Ok = false, Method = viaTimers ? "timers" : "row", Reason = "entity " + heart + " is not a castle heart or connected to one" };
        return ServantRuntime.Finish(world, h, missionGuid, viaTimers);
    }

    /// <summary>
    /// Move servants' inventories into the castle's containers through the inventory engine's
    /// <c>TransferAll</c> (conserving: never duplicates or destroys). <paramref name="servants"/>
    /// null: every servant at home (free, carrying items or injured). <paramref name="to"/>: one
    /// container of this castle; none: every ordinary open castle container in the castle's order.
    /// What does not fit stays on the servant and is reported as no room, unless
    /// <paramref name="discard"/> (test only) removes it, logged as a warning.
    /// </summary>
    public static ServantEmptyResult Empty(CastleId heart, IReadOnlyList<CastleId>? servants, CastleId to, bool discard)
    {
        var world = ServantRuntime.RequireWorld("Empty");
        var h = ServantRuntime.HeartOf(world.EntityManager, heart.ToEntity());
        if (h == Unity.Entities.Entity.Null)
            return new ServantEmptyResult { Ok = false, Reason = "entity " + heart + " is not a castle heart or connected to one" };
        return ServantRuntime.Empty(world, h, servants, to, discard);
    }

    /// <summary>The game's servant-timers debug setting and whether a finish restore is pending.</summary>
    public static ServantTimersStatus Timers() => ServantRuntime.TimersStatus(ServantRuntime.RequireWorld("Timers"));

    /// <summary>Set the game's servant-timers debug setting (true: timers disabled). Cancels a pending finish restore.</summary>
    public static ServantTimersStatus SetTimers(bool disabled) => ServantRuntime.SetTimers(ServantRuntime.RequireWorld("SetTimers"), disabled);
}

/// <summary>One hunt length choice of the game's mission settings.</summary>
public sealed class ServantDurationSlot
{
    /// <summary>The choice's index (<c>ServantSendRequest.DataSlot</c>).</summary>
    public int Slot { get; init; }

    /// <summary><c>ServantMissionSetting.MissionLength</c> in seconds.</summary>
    public float SettingSeconds { get; init; }

    /// <summary>The length a send writes now (times 1e-5 while servant timers are disabled).</summary>
    public float LengthSeconds { get; init; }
}

/// <summary>What <see cref="ServantBridge.Zones"/> found.</summary>
public sealed class ServantZonesReport
{
    /// <summary>The castle heart.</summary>
    public CastleId Heart { get; init; }

    /// <summary>Whose discovery was read (the heart's owner unless named).</summary>
    public CastleId User { get; init; }

    /// <summary>False when the user's progression could not be read (discovery unknown).</summary>
    public bool DiscoveryReadable { get; init; }

    /// <summary>The game's "all progression unlocked" debug setting: every zone counts as discovered.</summary>
    public bool AllProgressionUnlocked { get; init; }

    /// <summary>The game's servant-timers debug setting.</summary>
    public bool TimersDisabled { get; init; }

    /// <summary>Map zones with a servant mission on this map.</summary>
    public int ZonesWithMission { get; init; }

    /// <summary>The castle's active hunts.</summary>
    public int ActiveHunts { get; init; }

    /// <summary>The zones listed (discovered ones, or all).</summary>
    public IReadOnlyList<ServantZoneInfo> Zones { get; init; } = Array.Empty<ServantZoneInfo>();

    /// <summary>The hunt length choices.</summary>
    public IReadOnlyList<ServantDurationSlot> DurationSlots { get; init; } = Array.Empty<ServantDurationSlot>();
}

/// <summary>What <see cref="ServantBridge.Finish"/> did.</summary>
public sealed class ServantFinishResult
{
    /// <summary>True when the hunts were made due (or already were, with timers disabled).</summary>
    public bool Ok { get; init; }

    /// <summary><c>timers</c> or <c>row</c>.</summary>
    public string Method { get; init; } = "";

    /// <summary>What happened, in a sentence.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The hunts targeted, as they were before.</summary>
    public IReadOnlyList<ServantMission> Hunts { get; init; } = Array.Empty<ServantMission>();

    /// <summary>Method <c>row</c>: the rows as rewritten.</summary>
    public IReadOnlyList<ServantMission> Finished { get; init; } = Array.Empty<ServantMission>();

    /// <summary>Method <c>timers</c>: the timers were already disabled (nothing changed).</summary>
    public bool TimersWereDisabled { get; init; }

    /// <summary>Method <c>timers</c>: Progenitor turns the timers back on once the hunts are gone (at most 10 s).</summary>
    public bool RestorePending { get; init; }
}

/// <summary>The game's servant-timers debug setting as the bridge sees it.</summary>
public sealed class ServantTimersStatus
{
    /// <summary><c>SyncedServerDebugSettings.ServantTimersDisabled</c>; null when unreadable.</summary>
    public bool? TimersDisabled { get; init; }

    /// <summary>Before a set: the value it had.</summary>
    public bool? WasDisabled { get; init; }

    /// <summary>A finish is waiting to turn the timers back on.</summary>
    public bool RestorePending { get; init; }

    /// <summary>This set cancelled a pending finish restore.</summary>
    public bool CancelledRestore { get; init; }
}

/// <summary>What <see cref="ServantBridge.Empty"/> did.</summary>
public sealed class ServantEmptyResult
{
    /// <summary>False when the call was refused, or a servant could not be read or a transfer stopped. No room is not a failure.</summary>
    public bool Ok { get; init; }

    /// <summary>Why the call was refused, or "".</summary>
    public string Reason { get; init; } = "";

    /// <summary>One row per servant.</summary>
    public IReadOnlyList<ServantEmptyRow> Servants { get; init; } = Array.Empty<ServantEmptyRow>();

    /// <summary>The containers tried, in order.</summary>
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
}

/// <summary>One servant of a <see cref="ServantBridge.Empty"/>.</summary>
public sealed class ServantEmptyRow
{
    /// <summary>The servant.</summary>
    public CastleId Servant { get; init; }

    /// <summary>The coffin's servant name, or "".</summary>
    public string Name { get; init; } = "";

    /// <summary>Units the servant carried before.</summary>
    public int Before { get; init; }

    /// <summary>Units moved into containers.</summary>
    public int Moved { get; init; }

    /// <summary>Units still on the servant.</summary>
    public int Left { get; init; }

    /// <summary>Units removed by <c>discard</c> (test only), 0 otherwise.</summary>
    public int Discarded { get; init; }

    /// <summary>Where the units went: <c>container xN</c>.</summary>
    public IReadOnlyList<string> Into { get; init; } = Array.Empty<string>();

    /// <summary>What <c>discard</c> removed: <c>amount x item</c>.</summary>
    public IReadOnlyList<string> DiscardedItems { get; init; } = Array.Empty<string>();

    /// <summary>"" when everything moved; otherwise why not (no room, on a hunt, unreadable).</summary>
    public string Reason { get; init; } = "";
}
