using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Game-free clone + buffer + station-row world used by tests to pin apply, unapply,
/// vanilla safety and rollback. Live <see cref="RecipeApply"/> follows the same rules
/// against ECS; this type never touches a world.
/// </summary>
internal sealed class RecipeApplyWorld
{
    public readonly Dictionary<string, int> Names = new(StringComparer.Ordinal);
    public readonly HashSet<int> ItemGuids = new();
    public readonly HashSet<int> StationGuids = new();
    public readonly HashSet<int> SourceRecipeGuids = new();
    public readonly HashSet<int> VanillaRecipeGuids = new();
    public readonly Dictionary<int, byte[]> StationBuffers = new();
    public readonly Dictionary<int, RecipeCloneState> Clones = new();
    public readonly HashSet<int> RecipeMap = new();
    public readonly HashSet<int> ClonedHere = new();
    public int CloneDestroyCount;
    public int HashMapCapacity = 1024;

    /// <summary>
    /// When set to an apply-plan step, <see cref="RecipeApplySession.TryApply"/> completes
    /// that step and then fails, running rollback. Empty means run to completion.
    /// </summary>
    public string? FailAfter { get; set; }
}

internal sealed class RecipeCloneState
{
    public int GuidHash;
    public int SourceGuidHash;
    public byte[] Requirements = Array.Empty<byte>();
    public byte[] Outputs = Array.Empty<byte>();
    public byte[] Units = Array.Empty<byte>();
    public byte[] Links = Array.Empty<byte>();
    public float Duration;
    public bool AlwaysUnlocked;
    public bool Destroyed;
}

/// <summary>
/// Apply / unapply algebra over <see cref="RecipeApplyWorld"/>. Missing inputs or stations
/// refuse before a clone exists. A failure after the clone is created rolls the world back
/// to the captured baseline.
/// </summary>
internal static class RecipeApplySession
{
    public static bool TryApply(
        RecipeApplyWorld world,
        RecipeDefinition def,
        RecipeStationLedger ledger,
        out string report)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (def == null) throw new ArgumentNullException(nameof(def));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));

        var baseline = Capture(world);
        var sb = new StringBuilder();

        try
        {
            if (!TryResolveSource(world, def, out int sourceHash, out var sourceWhy))
            {
                report = sourceWhy;
                return false;
            }
            if (def.GuidHash == sourceHash || world.VanillaRecipeGuids.Contains(def.GuidHash))
            {
                report = "refusing to write a vanilla recipe GUID";
                return false;
            }

            if (!TryResolveItem(world, def.Output, out int outputHash, out var outputWhy))
            {
                report = "output: " + outputWhy;
                return false;
            }

            var inputHashes = new int[def.Inputs.Count];
            var inputAmounts = new int[def.Inputs.Count];
            for (int i = 0; i < def.Inputs.Count; i++)
            {
                if (!TryResolveItem(world, def.Inputs[i], out inputHashes[i], out var inWhy))
                {
                    report = "input " + i + ": " + inWhy;
                    return false;
                }
                inputAmounts[i] = def.Inputs[i].Amount;
            }

            var stationHashes = new int[def.Stations.Count];
            for (int i = 0; i < def.Stations.Count; i++)
            {
                if (!TryResolveStation(world, def.Stations[i], out stationHashes[i], out var stWhy))
                {
                    report = "station " + i + ": " + stWhy;
                    return false;
                }
            }

            if (world.Clones.TryGetValue(def.GuidHash, out var existing) && !existing.Destroyed)
            {
                if (!world.ClonedHere.Contains(def.GuidHash))
                {
                    report = "existing recipe GUID is vanilla or foreign; apply refused";
                    return false;
                }
            }
            else
            {
                existing = null;
            }

            if (world.RecipeMap.Count >= world.HashMapCapacity && !world.RecipeMap.Contains(def.GuidHash))
            {
                report = "RecipeHashLookupMap full (capacity " + world.HashMapCapacity + ")";
                return false;
            }

            RecipeCloneState clone;
            if (existing != null)
            {
                clone = existing;
                sb.Append("updated existing clone; ");
            }
            else
            {
                clone = new RecipeCloneState
                {
                    GuidHash = def.GuidHash,
                    SourceGuidHash = sourceHash,
                    Units = new byte[RecipeBufferLayout.OutputUnitBytes(1)],
                    Links = new byte[RecipeBufferLayout.LinkBytes(1)],
                };
                RecipeBufferLayout.WriteOutputUnit(clone.Units, 0, sourceHash, 1);
                RecipeBufferLayout.WriteLink(clone.Links, 0, sourceHash);
                world.Clones[def.GuidHash] = clone;
                world.ClonedHere.Add(def.GuidHash);
                sb.Append("clone; ");
            }

            if (FailedAfter(world, RecipeApplyPlan.CloneSource, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            clone.Duration = def.CraftDuration;
            clone.AlwaysUnlocked = def.AlwaysUnlocked;
            sb.Append("RecipeData duration=" + def.CraftDuration.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + "; ");
            if (FailedAfter(world, RecipeApplyPlan.WriteRecipeData, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            clone.Outputs = RecipeBufferLayout.EncodeOutputs(outputHash, def.Output.Amount);
            if (!RecipeBufferLayout.TryReadOutput(clone.Outputs, 0, out int outGuid, out int outAmt)
                || outGuid != outputHash || outAmt != def.Output.Amount)
            {
                Restore(world, baseline, ledger, def, stationHashes);
                report = sb + "output buffer readback mismatch";
                return false;
            }
            sb.Append("buffers output=1; ");
            if (FailedAfter(world, RecipeApplyPlan.RewriteOutputBuffer, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            clone.Requirements = RecipeBufferLayout.EncodeRequirements(inputHashes, inputAmounts);
            for (int i = 0; i < inputHashes.Length; i++)
            {
                if (!RecipeBufferLayout.TryReadRequirement(clone.Requirements, i, out int inGuid, out int inAmt)
                    || inGuid != inputHashes[i] || inAmt != inputAmounts[i])
                {
                    Restore(world, baseline, ledger, def, stationHashes);
                    report = sb + "requirement buffer readback mismatch";
                    return false;
                }
            }
            sb.Append("req=" + inputHashes.Length + "; ");
            if (FailedAfter(world, RecipeApplyPlan.RewriteRequirementBuffer, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            clone.Units = RecipeBufferLayout.ClearElements(
                clone.Units, RecipeBufferLayout.OutputUnitStride, RecipeBufferLayout.OutputUnitGuidOffset);
            clone.Links = RecipeBufferLayout.ClearElements(
                clone.Links, RecipeBufferLayout.LinkStride, RecipeBufferLayout.LinkGuidOffset);
            if (FailedAfter(world, RecipeApplyPlan.ClearOutputUnitBuffer, sb, out report)
                || FailedAfter(world, RecipeApplyPlan.ClearLinkBuffer, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            sb.Append("maps inserted; ");
            if (FailedAfter(world, RecipeApplyPlan.InsertPrefabMaps, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            if (!world.RecipeMap.Contains(def.GuidHash))
                world.RecipeMap.Add(def.GuidHash);
            sb.Append("recipeMap=True; ");
            if (FailedAfter(world, RecipeApplyPlan.InsertRecipeHashLookup, sb, out report)
                || FailedAfter(world, RecipeApplyPlan.InsertOriginalDurations, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            int ownerId = RecipeApply.OwnerId(def.Owner);
            for (int i = 0; i < stationHashes.Length; i++)
            {
                int station = stationHashes[i];
                if (!world.StationBuffers.TryGetValue(station, out var raw))
                    raw = Array.Empty<byte>();
                var rows = new List<int>(RecipeBufferLayout.DecodeWorkstationRows(raw));
                bool present = rows.Contains(def.GuidHash);
                if (ledger.RecordAppend(station, def.GuidHash, ownerId, present) && !present)
                    rows.Add(def.GuidHash);
                world.StationBuffers[station] = RecipeBufferLayout.EncodeWorkstationRows(rows);
                sb.Append("stationPrefab " + station + (present ? " claimed; " : " appended; "));
            }
            if (FailedAfter(world, RecipeApplyPlan.AppendStationPrefabRow, sb, out report)
                || FailedAfter(world, RecipeApplyPlan.AppendStationLiveInstances, sb, out report))
            {
                Restore(world, baseline, ledger, def, stationHashes);
                return false;
            }

            report = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            Restore(world, baseline, ledger, def, ResolvedStationHashes(world, def));
            report = sb + " EXCEPTION " + ex;
            return false;
        }
    }

    public static bool TryUnapply(
        RecipeApplyWorld world,
        RecipeDefinition def,
        RecipeStationLedger ledger,
        out string report)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (def == null) throw new ArgumentNullException(nameof(def));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));

        var sb = new StringBuilder();
        int ownerId = RecipeApply.OwnerId(def.Owner);
        for (int i = 0; i < def.Stations.Count; i++)
        {
            if (!TryResolveStation(world, def.Stations[i], out int station, out _))
                continue;
            bool removeRow = ledger.RecordRemove(station, def.GuidHash, ownerId);
            if (!removeRow || !world.StationBuffers.TryGetValue(station, out var raw))
            {
                sb.Append("stationPrefab row kept; ");
                continue;
            }
            var rows = new List<int>(RecipeBufferLayout.DecodeWorkstationRows(raw));
            for (int r = rows.Count - 1; r >= 0; r--)
            {
                if (rows[r] != def.GuidHash) continue;
                rows.RemoveAt(r);
                break;
            }
            world.StationBuffers[station] = RecipeBufferLayout.EncodeWorkstationRows(rows);
            sb.Append("stationPrefab row removed; ");
        }

        world.RecipeMap.Remove(def.GuidHash);
        sb.Append("recipeMap removed; ");

        if (world.Clones.TryGetValue(def.GuidHash, out var clone) && !clone.Destroyed)
        {
            clone.Destroyed = true;
            world.Clones.Remove(def.GuidHash);
            world.ClonedHere.Remove(def.GuidHash);
            world.CloneDestroyCount++;
            sb.Append("clone destroyed; ");
        }
        else
            sb.Append("no clone; ");

        report = sb.ToString();
        return true;
    }

    public static byte[] SnapshotStation(RecipeApplyWorld world, int stationGuidHash)
    {
        if (!world.StationBuffers.TryGetValue(stationGuidHash, out var raw) || raw == null)
            return Array.Empty<byte>();
        var copy = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
        return copy;
    }

    private static bool TryResolveSource(RecipeApplyWorld world, RecipeDefinition def, out int guid, out string why)
    {
        guid = 0;
        why = "";
        if (def.SourceGuidHash != 0)
        {
            if (!world.SourceRecipeGuids.Contains(def.SourceGuidHash))
            {
                why = "source prefab " + def.SourceGuidHash + " not found";
                return false;
            }
            guid = def.SourceGuidHash;
            return true;
        }
        if (!string.IsNullOrEmpty(def.SourceName) && world.Names.TryGetValue(def.SourceName, out guid))
        {
            if (!world.SourceRecipeGuids.Contains(guid))
            {
                why = "source prefab '" + def.SourceName + "' is not a recipe";
                return false;
            }
            return true;
        }
        why = "source prefab '" + def.SourceName + "' not found";
        return false;
    }

    private static bool TryResolveItem(RecipeApplyWorld world, RecipeItemRef item, out int guid, out string why)
    {
        guid = 0;
        why = "";
        if (item.GuidHash != 0)
        {
            if (!world.ItemGuids.Contains(item.GuidHash))
            {
                why = "item " + item.GuidHash + " not found";
                return false;
            }
            guid = item.GuidHash;
            return true;
        }
        if (!string.IsNullOrEmpty(item.Name) && world.Names.TryGetValue(item.Name, out guid)
            && world.ItemGuids.Contains(guid))
            return true;
        why = "item '" + item.Name + "' not found";
        guid = 0;
        return false;
    }

    private static bool TryResolveStation(RecipeApplyWorld world, RecipeStationRef station, out int guid, out string why)
    {
        guid = 0;
        why = "";
        if (station.GuidHash != 0)
        {
            if (!world.StationGuids.Contains(station.GuidHash))
            {
                why = "station " + station.GuidHash + " not found";
                return false;
            }
            guid = station.GuidHash;
            return true;
        }
        if (!string.IsNullOrEmpty(station.Name) && world.Names.TryGetValue(station.Name, out guid)
            && world.StationGuids.Contains(guid))
            return true;
        why = "station '" + station.Name + "' not found";
        guid = 0;
        return false;
    }

    private static bool FailedAfter(RecipeApplyWorld world, string step, StringBuilder sb, out string report)
    {
        if (!string.Equals(world.FailAfter, step, StringComparison.Ordinal))
        {
            report = "";
            return false;
        }
        report = sb + "injected failure after " + step;
        return true;
    }

    private sealed class WorldBaseline
    {
        public Dictionary<int, byte[]> StationBuffers = new();
        public Dictionary<int, RecipeCloneState> Clones = new();
        public HashSet<int> RecipeMap = new();
        public HashSet<int> ClonedHere = new();
        public int CloneDestroyCount;
    }

    private static WorldBaseline Capture(RecipeApplyWorld world)
    {
        var snap = new WorldBaseline
        {
            CloneDestroyCount = world.CloneDestroyCount,
        };
        foreach (var kv in world.StationBuffers)
            snap.StationBuffers[kv.Key] = Copy(kv.Value);
        foreach (var kv in world.Clones)
            snap.Clones[kv.Key] = Copy(kv.Value);
        foreach (int g in world.RecipeMap) snap.RecipeMap.Add(g);
        foreach (int g in world.ClonedHere) snap.ClonedHere.Add(g);
        return snap;
    }

    private static void Restore(
        RecipeApplyWorld world,
        WorldBaseline snap,
        RecipeStationLedger ledger,
        RecipeDefinition def,
        int[] stationHashes)
    {
        world.StationBuffers.Clear();
        foreach (var kv in snap.StationBuffers)
            world.StationBuffers[kv.Key] = Copy(kv.Value);
        world.Clones.Clear();
        foreach (var kv in snap.Clones)
            world.Clones[kv.Key] = Copy(kv.Value);
        world.RecipeMap.Clear();
        foreach (int g in snap.RecipeMap) world.RecipeMap.Add(g);
        world.ClonedHere.Clear();
        foreach (int g in snap.ClonedHere) world.ClonedHere.Add(g);
        world.CloneDestroyCount = snap.CloneDestroyCount;
        if (ledger == null || def == null || stationHashes == null) return;
        int ownerId = RecipeApply.OwnerId(def.Owner);
        for (int i = 0; i < stationHashes.Length; i++)
            ledger.RecordRemove(stationHashes[i], def.GuidHash, ownerId);
    }

    private static int[] ResolvedStationHashes(RecipeApplyWorld world, RecipeDefinition def)
    {
        var hashes = new int[def.Stations.Count];
        for (int i = 0; i < def.Stations.Count; i++)
            TryResolveStation(world, def.Stations[i], out hashes[i], out _);
        return hashes;
    }

    private static byte[] Copy(byte[] raw)
    {
        if (raw == null || raw.Length == 0) return Array.Empty<byte>();
        var copy = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
        return copy;
    }

    private static RecipeCloneState Copy(RecipeCloneState src) =>
        new()
        {
            GuidHash = src.GuidHash,
            SourceGuidHash = src.SourceGuidHash,
            Requirements = Copy(src.Requirements),
            Outputs = Copy(src.Outputs),
            Units = Copy(src.Units),
            Links = Copy(src.Links),
            Duration = src.Duration,
            AlwaysUnlocked = src.AlwaysUnlocked,
            Destroyed = src.Destroyed,
        };
}
