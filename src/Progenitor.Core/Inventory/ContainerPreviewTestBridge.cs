#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Cameras;

namespace Progenitor.Inventory;

/// <summary>
/// Dev bridge <c>container_preview</c> (reached by reflection from <c>Progenitor.Dev</c>): open, read and close a
/// container preview without a plugin, for the live experiments E1 to E5. Client: <c>Open</c>, <c>Close</c>,
/// <c>Status</c>, <c>BagMode</c>. Server: <c>ServerStatus</c> (every server-side preview and every shared streaming
/// lease). Main thread.
/// </summary>
internal static class ContainerPreviewTestBridge
{
    public static Dictionary<string, object?> Open(string id, bool keepOpen, double seconds, bool hideBag, double replicationTimeout, bool dropEvents, bool hudGate, bool syncInventory)
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("container_preview open runs on the client");
        if (!ContainerId.TryParse(id, out var container)) throw new ArgumentException("args.id must be a container id (c- plus 32 hex digits)");
        var options = new ContainerPreviewOptions
        {
            KeepOpen = keepOpen,
            Seconds = seconds,
            HidePlayerInventory = hideBag,
            ReplicationTimeoutSeconds = (float)replicationTimeout,
            DropItemEvents = dropEvents,
        };
        var problem = options.Problem();
        if (problem != null) throw new ArgumentException(problem);
        var handle = ContainerPreviewClient.Open(container, options, typeof(ContainerPreviewTestBridge), hudGate, syncInventory);
        var result = handle.Describe();
        result["requested"] = "open";
        return result;
    }

    public static Dictionary<string, object?> Close()
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("container_preview close runs on the client");
        var current = ContainerPreviewClient.Current;
        if (current == null) return new Dictionary<string, object?> { ["requested"] = "none", ["note"] = "no preview is running" };
        current.Dispose();
        var result = current.Describe();
        result["requested"] = "close";
        return result;
    }

    public static Dictionary<string, object?> Status()
    {
        if (Plugin.IsServerProcess) return ServerStatus();
        var world = Worlds.Client;
        var result = new Dictionary<string, object?>
        {
            ["side"] = "client",
            ["bagHide"] = ContainerPreviewClient.BagHideByAlpha ? "alpha" : "inactive",
            ["preview"] = ContainerPreviewClient.Current?.Describe(),
        };
        if (world != null && world.IsCreated)
        {
            var window = ContainerPreviewClient.ReadWindow(world);
            result["menu"] = window.Menu;
            result["mapperTarget"] = window.Mapper == null ? null : window.Target.Index + ":" + window.Target.Version;
            result["mapperInteractBuff"] = window.Mapper == null ? null : window.Interact.Index + ":" + window.Interact.Version;
        }
        return result;
    }

    public static Dictionary<string, object?> BagMode(string mode)
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("container_preview bag runs on the client");
        ContainerPreviewClient.BagHideByAlpha = mode switch
        {
            "alpha" => true,
            "inactive" => false,
            _ => throw new ArgumentException("args.mode must be inactive or alpha"),
        };
        return new Dictionary<string, object?> { ["bagHide"] = mode, ["note"] = "applies to the next preview that opens" };
    }

    /// <summary>E1b persistence read only: turn the world-save guard of the inventory sync off or on (server).</summary>
    public static Dictionary<string, object?> Guard(bool on)
    {
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("container_preview guard runs on the server");
        ContainerPreviewServer.SaveGuardEnabled = on;
        Plugin.Logger?.LogWarning("ContainerPreview: save guard turned " + (on ? "on" : "off") + " by the Dev bridge.");
        var result = ServerStatus();
        result["requested"] = "guard";
        return result;
    }

    /// <summary>
    /// Dev-only one-off repair (server): put a user's streaming origin (<c>User.ProxySyncPosition</c>) back to zero, for an
    /// origin left over from before the lease records existed. Refused while a lease of this run holds that user.
    /// </summary>
    public static Dictionary<string, object?> ProxyReset(string platform)
    {
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("container_preview proxy_reset runs on the server");
        if (!ulong.TryParse((platform ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) || id == 0)
            throw new ArgumentException("args.user must be the user's platform id (a SteamID64)");
        var result = ObservationGuard.ResetToZero(id);
        result["requested"] = "proxy_reset";
        return result;
    }

    public static Dictionary<string, object?> ServerStatus()
    {
        var leases = new List<Dictionary<string, object?>>();
        foreach (var lease in SharedObservation.All())
            leases.Add(new Dictionary<string, object?>
            {
                ["user"] = lease.User.Index + ":" + lease.User.Version,
                ["point"] = new[] { lease.Point.x, lease.Point.y, lease.Point.z },
                ["holders"] = lease.HolderCount,
                ["proxySuspended"] = lease.ProxySuspended,
            });
        return new Dictionary<string, object?>
        {
            ["side"] = "server",
            ["previews"] = ContainerPreviewServer.Describe(),
            ["syncReleasing"] = ContainerPreviewServer.SyncBookkeeping.Releasing,
            ["syncSweepPending"] = ContainerPreviewServer.SyncBookkeeping.SweepPending,
            ["syncRecords"] = ContainerPreviewServer.SyncBookkeeping.Records,
            ["saveGuard"] = ContainerPreviewServer.SaveGuardEnabled,
            ["saveSuspended"] = ContainerPreviewServer.SaveSuspended,
            ["worldSaveHookArmed"] = Persistence.ModSave.WorldSaveHookArmed,
            ["observationRecords"] = ObservationGuard.RecordCount,
            ["observationSweepPending"] = ObservationGuard.SweepPending,
            ["leases"] = leases,
        };
    }
}
#endif
