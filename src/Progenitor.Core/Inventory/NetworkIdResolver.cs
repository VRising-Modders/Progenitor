using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Inventory;

/// <summary>
/// The three words of a game <c>NetworkId</c> as a client reports them
/// (<c>VanillaPanelTarget.NetworkIdIndex</c>, <c>NetworkIdGeneration</c>, <c>NetworkId2</c>), and
/// the checks every value from a client goes through before it is looked up. Game-free.
/// </summary>
internal static class NetworkIdWords
{
    /// <summary><c>NetworkIdType.None</c>.</summary>
    internal const int TypeNone = 0;

    /// <summary><c>NetworkIdType.Normal</c>: every placed container, station and heart.</summary>
    internal const int TypeNormal = 1;

    /// <summary><c>NetworkIdType.MegaStatic</c>: a static map object.</summary>
    internal const int TypeStatic = 2;

    /// <summary><c>Normal_Generation</c> is a byte.</summary>
    internal const int MaxGeneration = 255;

    /// <summary>
    /// True when the words can name a placed building: type normal, index positive, generation
    /// 0..255. False with a sentence otherwise. Never throws.
    /// </summary>
    internal static bool Validate(int index, int generation, int type, out string reason)
    {
        if (type == TypeNone)
        {
            reason = "the network id is empty (type 0): the client had no network id for its target";
            return false;
        }

        if (type == TypeStatic)
        {
            reason = "the network id names a static map object (type 2), not a placed container, station or castle heart";
            return false;
        }

        if (type != TypeNormal)
        {
            reason = "network id type " + type.ToString(CultureInfo.InvariantCulture) + " is not a type the game uses (0, 1 or 2)";
            return false;
        }

        if (index <= 0)
        {
            reason = "network id index " + index.ToString(CultureInfo.InvariantCulture) + " is out of range (it must be positive)";
            return false;
        }

        if (generation < 0 || generation > MaxGeneration)
        {
            reason = "network id generation " + generation.ToString(CultureInfo.InvariantCulture) + " is out of range (0..255)";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary><c>index:generation</c>, the form the logs use.</summary>
    internal static string Format(int index, int generation) =>
        index.ToString(CultureInfo.InvariantCulture) + ":" + generation.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One networked entity the server scan found: its normal id and its entity now.</summary>
internal readonly struct NetworkIdEntry
{
    /// <summary>Construct an entry.</summary>
    public NetworkIdEntry(int index, int generation, int entityIndex, int entityVersion)
    {
        Index = index;
        Generation = generation;
        EntityIndex = entityIndex;
        EntityVersion = entityVersion;
    }

    /// <summary><c>NetworkId.Normal_Index</c>.</summary>
    public int Index { get; }

    /// <summary><c>NetworkId.Normal_Generation</c>.</summary>
    public int Generation { get; }

    /// <summary><c>Entity.Index</c> in the current world load.</summary>
    public int EntityIndex { get; }

    /// <summary><c>Entity.Version</c> in the current world load.</summary>
    public int EntityVersion { get; }
}

/// <summary>What the resolver reads from the world. The server implements it; tests fake it.</summary>
internal interface INetworkIdSource
{
    /// <summary>Append every candidate entity (placed models with an inventory, castle hearts) that carries a normal network id.</summary>
    void Scan(List<NetworkIdEntry> into);

    /// <summary>
    /// The entity's network id now: false when the entity no longer exists or has no network id.
    /// The resolver calls this on every hit, so a cached row is never trusted on its own.
    /// </summary>
    bool TryReadBack(int entityIndex, int entityVersion, out int index, out int generation, out int type);
}

/// <summary>
/// Maps a client-reported network id to the server entity it names. Game-free.
///
/// <para><b>How.</b> A map from <c>Normal_Index</c> to (generation, entity) built from one scan of
/// the candidate entities' own <c>NetworkId</c> components, which is the data the game's network id
/// map is filled from. A hit is confirmed by reading the entity's id back; a miss or a stale row
/// rebuilds the map, at most once per <see cref="MinRebuildMs"/> however many requests miss, so a
/// client that sends made-up ids cannot make the server scan every frame. The map is dropped when
/// the world epoch changes (network ids are allocated per session and per world load).</para>
/// </summary>
internal sealed class NetworkIdResolver
{
    /// <summary>Smallest gap between two rebuilds.</summary>
    internal const long MinRebuildMs = 250;

    private readonly INetworkIdSource _source;
    private readonly Dictionary<int, NetworkIdEntry> _byIndex = new();
    private readonly List<NetworkIdEntry> _scratch = new();
    private int _epoch = int.MinValue;
    private long _lastRebuild = long.MinValue;

    /// <summary>Construct over a source.</summary>
    internal NetworkIdResolver(INetworkIdSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Rows in the map.</summary>
    internal int Count => _byIndex.Count;

    /// <summary>How many rebuilds ran (diagnostics and tests).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>Forget everything (Core uninstall, a world change).</summary>
    internal void Clear()
    {
        _byIndex.Clear();
        _epoch = int.MinValue;
        _lastRebuild = long.MinValue;
    }

    /// <summary>
    /// Resolve the words to an entity of world <paramref name="epoch"/>. False with a sentence on
    /// malformed words, an id no candidate carries, a stale generation, or a source that threw.
    /// Never throws.
    /// </summary>
    internal bool TryResolve(int index, int generation, int type, int epoch, long nowMs,
        out int entityIndex, out int entityVersion, out string reason)
    {
        entityIndex = entityVersion = 0;
        if (!NetworkIdWords.Validate(index, generation, type, out reason)) return false;
        string net = NetworkIdWords.Format(index, generation);
        try
        {
            if (epoch != _epoch)
            {
                _byIndex.Clear();
                _epoch = epoch;
                _lastRebuild = long.MinValue;
            }

            if (_byIndex.TryGetValue(index, out var row))
            {
                // The row is judged by its own read-back, never by the request: a forged generation
                // must not evict a valid row (legitimate lookups would miss until the next rebuild).
                if (StillValid(row))
                {
                    if (row.Generation == generation) return Found(row, out entityIndex, out entityVersion, out reason);
                    reason = Stale(net, index, row.Generation);
                    return false;
                }

                _byIndex.Remove(index);
            }

            if (_lastRebuild != long.MinValue && nowMs - _lastRebuild < MinRebuildMs)
            {
                reason = "no placed container, station or castle heart has network id " + net
                    + " (the lookup was refreshed " + (nowMs - _lastRebuild).ToString(CultureInfo.InvariantCulture)
                    + " ms ago; a container placed since then is found on a later request)";
                return false;
            }

            Rebuild(nowMs);
            if (_byIndex.TryGetValue(index, out row))
            {
                if (StillValid(row))
                {
                    if (row.Generation == generation) return Found(row, out entityIndex, out entityVersion, out reason);
                    reason = Stale(net, index, row.Generation);
                    return false;
                }

                _byIndex.Remove(index);
            }

            reason = "no placed container, station or castle heart has network id " + net
                + " (it was destroyed, is from an earlier session or world load, or names something else)";
            return false;
        }
        catch (Exception e)
        {
            reason = "the network id lookup failed: " + e.Message;
            return false;
        }
    }

    /// <summary>True while the row's entity still carries the row's own normal id.</summary>
    private bool StillValid(NetworkIdEntry row) =>
        _source.TryReadBack(row.EntityIndex, row.EntityVersion, out int i, out int g, out int t)
        && t == NetworkIdWords.TypeNormal && i == row.Index && g == row.Generation;

    private static string Stale(string net, int index, int current) =>
        "network id " + net + " is stale: index " + index.ToString(CultureInfo.InvariantCulture)
        + " now carries generation " + current.ToString(CultureInfo.InvariantCulture)
        + " (the object the client saw was destroyed and the id reused)";

    private static bool Found(NetworkIdEntry row, out int entityIndex, out int entityVersion, out string reason)
    {
        entityIndex = row.EntityIndex;
        entityVersion = row.EntityVersion;
        reason = "";
        return true;
    }

    /// <summary>
    /// Build the map now for <paramref name="epoch"/>, from a caller that is not under time pressure
    /// (a frame tick), so the first lookup of a world load does not pay the scan. Never throws.
    /// </summary>
    internal void Prime(int epoch, long nowMs)
    {
        try
        {
            _byIndex.Clear();
            _epoch = epoch;
            Rebuild(nowMs);
            Primes++;
        }
        catch
        {
            _byIndex.Clear();
            _lastRebuild = long.MinValue;
        }
    }

    /// <summary>How many of <see cref="Rebuilds"/> were <see cref="Prime"/> calls (the rest ran on a lookup miss).</summary>
    internal int Primes { get; private set; }

    private void Rebuild(long nowMs)
    {
        _lastRebuild = nowMs;
        Rebuilds++;
        _byIndex.Clear();
        _scratch.Clear();
        _source.Scan(_scratch);
        foreach (var e in _scratch)
        {
            if (e.Index <= 0) continue;
            // Live normal ids are unique by index; on a duplicate (an entity mid-destroy) keep the first.
            _byIndex.TryAdd(e.Index, e);
        }

        _scratch.Clear();
    }
}
