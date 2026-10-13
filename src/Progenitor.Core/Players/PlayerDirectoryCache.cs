using System;
using System.Collections.Generic;

namespace Progenitor.Players;

/// <summary>
/// Process-local directory cache: rows stamped with a world epoch and a revision that bumps
/// on join, disconnect, rename, world replacement and reload. Game-free so tests drive the
/// same freshness rules the live scanner uses.
/// </summary>
public sealed class PlayerDirectoryCache
{
    private readonly object _gate = new();
    private int _revision = 1;
    private int _epoch;
    private string _lastReason = "";
    private PlayerDirectoryRow[] _rows = Array.Empty<PlayerDirectoryRow>();

    /// <summary>Current cache generation. Starts at 1 so a default 0 stamp is never fresh.</summary>
    public int Revision
    {
        get { lock (_gate) return _revision; }
    }

    /// <summary>World epoch of the last <see cref="Replace"/>, or 0 before the first fill.</summary>
    public int Epoch
    {
        get { lock (_gate) return _epoch; }
    }

    /// <summary>Last <see cref="Invalidate"/> reason, empty when never invalidated.</summary>
    public string LastInvalidateReason
    {
        get { lock (_gate) return _lastReason; }
    }

    /// <summary>How many rows the cache currently holds.</summary>
    public int Count
    {
        get { lock (_gate) return _rows.Length; }
    }

    /// <summary>
    /// Bump the revision and drop retained rows. Call on connect, disconnect, world load, and
    /// Core reload. Rename is detected inside <see cref="Replace"/>.
    /// </summary>
    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            _revision++;
            if (_revision == 0) _revision = 1;
            _lastReason = reason ?? "";
            _rows = Array.Empty<PlayerDirectoryRow>();
            _epoch = 0;
        }
    }

    /// <summary>
    /// Replace the cached rows for <paramref name="worldEpoch"/>. When an account id keeps a
    /// different display name than the previous fill, the revision bumps again for rename.
    /// </summary>
    public void Replace(int worldEpoch, IReadOnlyList<PlayerDirectoryRow> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var copy = new PlayerDirectoryRow[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            copy[i] = rows[i];

        lock (_gate)
        {
            if (PlayerDirectoryLogic.DetectedRename(_rows, copy))
            {
                _revision++;
                if (_revision == 0) _revision = 1;
                _lastReason = PlayerDirectoryLogic.ReasonRename;
            }

            _epoch = worldEpoch;
            _rows = copy;
        }
    }

    /// <summary>Point-in-time view: epoch, revision, and a copy of the rows.</summary>
    public PlayerDirectorySnapshot Capture()
    {
        lock (_gate)
        {
            var copy = new PlayerDirectoryRow[_rows.Length];
            Array.Copy(_rows, copy, _rows.Length);
            return new PlayerDirectorySnapshot(_epoch, _revision, copy);
        }
    }

    /// <summary>Test / install seam: reset to revision 1 with no rows.</summary>
    public void ResetForTests()
    {
        lock (_gate)
        {
            _revision = 1;
            _epoch = 0;
            _lastReason = "";
            _rows = Array.Empty<PlayerDirectoryRow>();
        }
    }
}

/// <summary>Immutable view of the directory at one stamp.</summary>
public readonly struct PlayerDirectorySnapshot
{
    /// <summary>Construct.</summary>
    public PlayerDirectorySnapshot(int worldEpoch, int revision, IReadOnlyList<PlayerDirectoryRow> rows)
    {
        WorldEpoch = worldEpoch;
        Revision = revision;
        Rows = rows ?? Array.Empty<PlayerDirectoryRow>();
    }

    /// <summary>World epoch when the rows were filled.</summary>
    public int WorldEpoch { get; }

    /// <summary>Cache revision when the rows were filled.</summary>
    public int Revision { get; }

    /// <summary>Rows at that stamp.</summary>
    public IReadOnlyList<PlayerDirectoryRow> Rows { get; }

    /// <summary>True when this snapshot still matches the live epoch and revision.</summary>
    public bool IsFresh(int currentEpoch, int currentRevision) =>
        PlayerDirectoryLogic.IsFresh(WorldEpoch, Revision, currentEpoch, currentRevision);
}
