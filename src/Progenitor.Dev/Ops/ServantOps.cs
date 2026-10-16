using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Progenitor.Castles;
using Progenitor.Servants;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>servants</c> (server, main thread): a castle's servants and hunts through
/// <see cref="ServantBridge"/> (the same code as <c>ctx.Servants</c>), a send, making an active
/// hunt end now, and the game's servant-timers debug setting. Arguments: <see cref="ServantArgs"/>.
/// Ids are <c>index:version</c> strings of this world load; the castle is named by territory
/// index or heart.
/// </summary>
internal static class ServantOps
{
    public static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("servants runs on the server (hunts are server-authoritative); send it to the server bridge (27342)");
        var req = ServantArgs.Parse(args);
        if (req.Action == ServantArgs.Action.Timers)
        {
            var st = req.TimersOff == null ? ServantBridge.Timers() : ServantBridge.SetTimers(req.TimersOff.Value);
            return new
            {
                action = "timers",
                timersDisabled = st.TimersDisabled,
                wasDisabled = st.WasDisabled,
                restorePending = st.RestorePending,
                cancelledRestore = st.CancelledRestore,
                note = "server-wide: with timers disabled the game returns every active hunt on its next update and new sends last 1e-5 of their length; the game saves this setting with the world",
            };
        }

        var (heart, territory) = ResolveHeart(req);
        switch (req.Action)
        {
            case ServantArgs.Action.List:
            {
                var servants = new List<object>();
                int row = 1;
                foreach (var s in ServantBridge.List(heart)) servants.Add(ServantRow(s, row++));
                var hunts = new List<object>();
                foreach (var m in ServantBridge.Missions(heart)) hunts.Add(HuntRow(m));
                return new { action = "list", heart = heart.ToString(), territory, servants, hunts, timersDisabled = ServantBridge.Timers().TimersDisabled };
            }
            case ServantArgs.Action.Zones:
            {
                var report = ServantBridge.Zones(heart, req.User, req.All);
                return ZonesJson(report, territory, req.All);
            }
            case ServantArgs.Action.Send:
                return Send(req, heart, territory);
            case ServantArgs.Action.Empty:
                return Empty(req, heart, territory);
            default:
            {
                var r = ServantBridge.Finish(heart, req.Mission, req.ViaTimers);
                var hunts = new List<object>();
                foreach (var m in r.Hunts) hunts.Add(HuntRow(m));
                var finished = new List<object>();
                foreach (var m in r.Finished) finished.Add(HuntRow(m));
                return new
                {
                    action = "finish", ok = r.Ok, method = r.Method, reason = r.Reason, heart = heart.ToString(), territory,
                    hunts, finished, timersWereDisabled = r.TimersWereDisabled, restorePending = r.RestorePending,
                    note = r.Ok
                        ? "the game rolls each servant's success at the return as for any hunt: a servant can die or come back injured. Read the result with action list (or the servants: hunt returned log line)"
                        : null,
                };
            }
        }
    }

    private static object Send(ServantArgs.Request req, CastleId heart, int territory)
    {
        var list = ServantBridge.List(heart);
        List<CastleId> servants;
        if (req.Servants != null)
        {
            servants = ServantArgs.Resolve(list, req.Servants, out string error) ?? throw new ArgumentException(error);
        }
        else
        {
            servants = ServantArgs.FreeServants(list, 3);
            if (servants.Count == 0) throw new InvalidOperationException("the castle has no free servant (home, unhurt, empty inventory); name args.servants to see the game's refusal");
        }

        int mission = req.Mission;
        string picked = "";
        MissionZone zone = MissionZone.None;
        if (mission == 0 && req.Zone != null)
        {
            zone = req.Zone.Value;
            mission = ServantBridge.MissionOf(zone);
            if (mission == 0) throw new ArgumentException("zone " + zone + " has no servant mission (action zones lists them)");
        }
        else if (mission == 0)
        {
            var report = ServantBridge.Zones(heart, req.User, all: false);
            int count = req.Servants != null ? servants.Count : 1;
            var z = ServantBridge.PickZone(report, count, out string why)
                ?? throw new InvalidOperationException("no zone to pick: " + why);
            mission = z.MissionGuid;
            zone = z.Zone;
            picked = zone + " (" + (z.MissionName.Length > 0 ? z.MissionName : mission.ToString(CultureInfo.InvariantCulture)) + ", " + z.ServantSlots + " slot(s))";
            if (req.Servants == null && servants.Count > z.ServantSlots) servants = servants.GetRange(0, z.ServantSlots);
        }

        var result = ServantBridge.Send(new ServantSendRequest
        {
            Heart = heart,
            Servants = servants,
            MissionGuid = mission,
            DataSlot = req.Slot,
            User = req.User,
            CheckDiscovered = req.CheckDiscovered,
        });
        var sent = new List<string>();
        foreach (var s in result.Servants) sent.Add(s.ToString());
        return new
        {
            action = "send",
            ok = result.Ok,
            refusal = result.Ok ? null : result.Refusal.ToString(),
            reason = result.Ok ? null : result.Reason,
            summary = result.ToString(),
            heart = heart.ToString(),
            territory,
            mission = result.MissionGuid,
            zone = result.Zone.IsNone ? (zone.IsNone ? null : zone.ToString()) : result.Zone.ToString(),
            picked = picked.Length > 0 ? picked : null,
            slot = result.DataSlot,
            lengthSeconds = result.Ok ? result.LengthSeconds : (float?)null,
            startedUtc = result.Ok ? Utc(result.StartedUtc) : null,
            servants = sent,
            buffApplied = result.Ok ? result.BuffApplied : (bool?)null,
        };
    }

    private static object Empty(ServantArgs.Request req, CastleId heart, int territory)
    {
        List<CastleId>? servants = null;
        if (req.Servants != null)
            servants = ServantArgs.Resolve(ServantBridge.List(heart), req.Servants, out string error) ?? throw new ArgumentException(error);
        var r = ServantBridge.Empty(heart, servants, req.To, req.Discard);
        var rows = new List<object>();
        foreach (var s in r.Servants)
            rows.Add(new
            {
                servant = s.Servant.ToString(),
                name = s.Name,
                before = s.Before,
                moved = s.Moved,
                left = s.Left,
                discarded = req.Discard ? s.Discarded : (int?)null,
                into = s.Into,
                discardedItems = req.Discard ? s.DiscardedItems : null,
                reason = s.Reason.Length > 0 ? s.Reason : null,
            });
        return new
        {
            action = "empty",
            ok = r.Ok,
            reason = r.Reason.Length > 0 ? r.Reason : null,
            heart = heart.ToString(),
            territory,
            to = req.To.IsNone ? null : req.To.ToString(),
            discard = req.Discard,
            targets = r.Targets,
            servants = rows,
        };
    }

    private static (CastleId Heart, int Territory) ResolveHeart(ServantArgs.Request req)
    {
        if (req.Territory != null)
        {
            var castle = Progenitor.Castles.Castles.ForTerritory(req.Territory.Value);
            if (!castle.Enabled || castle.Heart.IsNone)
                throw new InvalidOperationException("territory " + req.Territory.Value + " has no castle (" + castle.DisabledReason + ")");
            return (castle.Heart, req.Territory.Value);
        }
        var heart = ServantBridge.HeartOf(req.Heart!.Value);
        if (heart.IsNone) throw new InvalidOperationException("entity " + req.Heart.Value + " is not a castle heart or connected to one");
        var c = Progenitor.Castles.Castles.ForHeart(heart);
        return (heart, c.Enabled ? c.Territory.Index : -1);
    }

    private static object ServantRow(ServantInfo s, int row) => new
    {
        row = "#" + row,
        name = s.Name,
        state = s.State.ToString(),
        servant = s.Servant.IsNone ? null : s.Servant.ToString(),
        coffin = s.Coffin.ToString(),
        injuryEndsUtc = s.InjuryEndsUtc == null ? null : Utc(s.InjuryEndsUtc.Value),
        injury = s.Injury,
        proficiency = s.HuntProficiency,
        lootFactor = s.LootFactor,
        gearLevel = s.GearLevel,
        expertise = s.Expertise,
        perks = new[] { s.Perk0, s.Perk1 },
        itemStacks = s.ItemStacks,
    };

    private static object HuntRow(ServantMission m)
    {
        var servants = new List<string>();
        foreach (var s in m.Servants) servants.Add(s.ToString());
        double left = (m.EndsUtc - DateTime.UtcNow).TotalSeconds;
        return new
        {
            row = m.Row,
            mission = m.MissionGuid,
            missionName = m.MissionName,
            zone = m.Zone.IsNone ? null : m.Zone.ToString(),
            slot = m.DataSlot,
            lengthSeconds = m.LengthSeconds,
            startedUtc = Utc(m.StartedUtc),
            endsUtc = Utc(m.EndsUtc),
            secondsLeft = Math.Round(left, 1),
            servants,
        };
    }

    private static object ZonesJson(ServantZonesReport r, int territory, bool all)
    {
        var zones = new List<object>();
        foreach (var z in r.Zones)
            zones.Add(new
            {
                zone = z.Zone.ToString(),
                mission = z.MissionGuid,
                missionName = z.MissionName,
                discovered = z.Discovered,
                active = z.Active,
                servantSlots = z.ServantSlots,
            });
        var slots = new List<object>();
        foreach (var s in r.DurationSlots)
            slots.Add(new { slot = s.Slot, settingSeconds = s.SettingSeconds, lengthSeconds = s.LengthSeconds });
        return new
        {
            action = "zones",
            heart = r.Heart.ToString(),
            territory,
            user = r.User.IsNone ? null : r.User.ToString(),
            all,
            discoveryReadable = r.DiscoveryReadable,
            allProgressionUnlocked = r.AllProgressionUnlocked,
            timersDisabled = r.TimersDisabled,
            activeHunts = r.ActiveHunts,
            zonesWithMission = r.ZonesWithMission,
            count = zones.Count,
            zones,
            durationSlots = slots,
        };
    }

    private static string? Utc(DateTime t) =>
        t == DateTime.MinValue ? null : t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
