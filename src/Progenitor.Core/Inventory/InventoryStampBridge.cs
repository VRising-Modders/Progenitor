using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// What the Dev bridge's <c>inventory_stamp</c> op read: the stamps of the named containers, the
/// clock, the optional <c>AnyChangedSince</c> answer, and the stamp service's counters.
/// </summary>
public sealed class InventoryStampReport
{
    internal InventoryStampReport() { }

    /// <summary><c>ctx.Inventory.Clock</c> before the batch.</summary>
    public long Clock { get; internal set; }

    /// <summary>One stamp per requested container, in request order.</summary>
    public IReadOnlyList<long> Stamps { get; internal set; } = Array.Empty<long>();

    /// <summary>The entity that holds each container's slots (<c>index:version</c>), or "" when it did not resolve.</summary>
    public IReadOnlyList<string> Inventories { get; internal set; } = Array.Empty<string>();

    /// <summary><c>AnyChangedSince(containers, since)</c>, when a <c>since</c> was given (asked before the stamps).</summary>
    public bool? ChangedSince { get; internal set; }

    /// <summary>Wall time of the <c>ChangeStamps</c> batch, microseconds.</summary>
    public double BatchMicroseconds { get; internal set; }

    /// <summary>Audit checks where the content changed under an unchanged stamp (should stay 0).</summary>
    public long Misses { get; internal set; }

    /// <summary>Audit checks performed since Core loaded.</summary>
    public long Audits { get; internal set; }

    /// <summary>Stamps handed out fresh because a read failed.</summary>
    public long FreshStamps { get; internal set; }

    /// <summary>Owner-to-inventory resolutions (cache misses).</summary>
    public long Resolves { get; internal set; }

    /// <summary>Batches whose job completion failed (their answers were fresh).</summary>
    public long CompletionFailures { get; internal set; }

    /// <summary>Why the raw reads are disabled for this process (a failed self-test), or "" while they are on.</summary>
    public string Disabled { get; internal set; } = "";

    /// <summary>Whether the first raw read was compared with the interop's GetChunk yet.</summary>
    public bool SelfTested { get; internal set; }

    /// <summary>Times the clock resumed with a rebase (idle window, Core reload, new store): every stamp read as changed once.</summary>
    public long Resumes { get; internal set; }

    /// <summary>Whether the audit is running (a stamp user other than Watch called lately).</summary>
    public bool Auditing { get; internal set; }

    /// <summary>References in the identity cache.</summary>
    public int Entries { get; internal set; }

    /// <summary>Rescans of an archetype's type list by the raw reader.</summary>
    public long TypeScans { get; internal set; }

    /// <summary>Raw reads that reported unknown, by the check that failed (only reasons with a count).</summary>
    public IReadOnlyDictionary<string, long> Failures { get; internal set; } = new Dictionary<string, long>();
}

/// <summary>
/// The Dev bridge's <c>inventory_stamp</c> op over <c>ctx.Inventory.ChangeStamp</c>. Public only
/// because Core grants <c>Progenitor.Dev</c> no <c>InternalsVisibleTo</c>; not a plugin API
/// (plugins use <c>ctx.Inventory</c>). Server and main thread only: throws
/// <see cref="InvalidOperationException"/> elsewhere, like <c>ctx.Inventory</c>.
/// </summary>
public static class InventoryStampBridge
{
    /// <summary>
    /// Stamp each container (owner <c>index</c>, <c>version</c>, inventory <c>instance</c>, all of the
    /// current world load) in one batch; with <paramref name="since"/>, first ask
    /// <c>AnyChangedSince</c> for the same list.
    /// </summary>
    public static InventoryStampReport Read(IReadOnlyList<(int Index, int Version, int Instance)> containers, long? since)
    {
        if (containers == null) throw new ArgumentNullException(nameof(containers));
        if (!Plugin.IsServerProcess || !InventoryRuntime.Installed)
            throw new InvalidOperationException("inventory_stamp runs on the server side only; inventories are server-authoritative.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("inventory_stamp must run on the game main thread.");

        var stamps = InventoryRuntime.Stamps;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        var refs = new InventoryRef[containers.Count];
        for (int i = 0; i < refs.Length; i++)
            refs[i] = new InventoryRef(containers[i].Index, containers[i].Version, containers[i].Instance, epoch);

        var report = new InventoryStampReport { Clock = stamps.Clock };
        if (since.HasValue) report.ChangedSince = stamps.AnyChangedSince(refs, since.Value);

        var into = new long[refs.Length];
        var watch = Stopwatch.StartNew();
        stamps.Stamps(refs, into);
        watch.Stop();
        report.BatchMicroseconds = watch.Elapsed.TotalMilliseconds * 1000.0;
        report.Stamps = into;

        var inventories = new string[refs.Length];
        var world = Worlds.Server;
        for (int i = 0; i < refs.Length; i++)
        {
            inventories[i] = "";
            if (world == null || !world.IsCreated) continue;
            try
            {
                if (UnityInventoryBackend.TryInventoryEntity(world.EntityManager, refs[i].Owner, refs[i].Instance, out Entity inv, out _))
                    inventories[i] = inv.Index + ":" + inv.Version;
            }
            catch
            {
                // Reported as unresolved.
            }
        }

        report.Inventories = inventories;
        report.Misses = stamps.Misses;
        report.Audits = stamps.Audits;
        report.FreshStamps = stamps.FreshStamps;
        report.Resolves = stamps.Resolves;
        report.CompletionFailures = stamps.CompletionFailures;
        report.Disabled = ChunkVersions.DisabledReason ?? "";
        report.SelfTested = ChunkVersions.SelfTested;
        report.Auditing = stamps.HasUser;
        report.Resumes = stamps.Resumes;
        report.Entries = stamps.Entries;
        report.TypeScans = ChunkVersions.TypeScans;
        var failures = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ChunkVersions.Failure reason in Enum.GetValues(typeof(ChunkVersions.Failure)))
        {
            if (reason == ChunkVersions.Failure.None) continue;
            long n = ChunkVersions.Failures(reason);
            if (n > 0) failures[reason.ToString()] = n;
        }

        report.Failures = failures;
        return report;
    }
}
