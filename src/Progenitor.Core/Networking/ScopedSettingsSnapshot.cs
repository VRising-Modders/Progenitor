using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Pure receipt accumulator for already wire-validated packets. Only kinds 1/2/3 participate.
/// Read Rows only when Accept returns true; the caller owns its last displayed snapshot.
/// Metadata describes the current candidate (or most recently completed snapshot). Rows remains
/// empty until completion. A fresh begin replaces that candidate; Reset discards everything.
/// now is finite monotonic time in seconds. Expiration is checked on every Accept, including
/// unrelated packets, and is absolute from begin, never extended by progress or duplicate begin.
/// No timer or callbacks are retained. Wire validation and authoritative delta handling remain external.
/// </summary>
internal sealed class ScopedSettingsSnapshot
{
    internal const int MaxRows = 512;
    internal const double TimeoutSeconds = 30;
    private readonly List<ScopedSettingRecord> _incoming = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private bool _collecting;
    private double _started;

    internal IReadOnlyList<ScopedSettingRecord> Rows { get; private set; } = Array.Empty<ScopedSettingRecord>();
    internal bool CanEdit { get; private set; }
    internal string ServerName { get; private set; } = "";
    internal Guid Generation { get; private set; }
    internal int BeginCount { get; private set; }

    /// <summary>Whether a begun snapshot is still arriving at <paramref name="now"/>; an expired one is discarded.</summary>
    internal bool Collecting(double now)
    {
        if (_collecting && (double.IsNaN(now) || double.IsInfinity(now) || now < _started || now - _started >= TimeoutSeconds)) Reset();
        return _collecting;
    }

    internal bool Accept(ScopedSettingsPacket packet, double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) { Reset(); return false; }
        if (_collecting && (now < _started || now - _started >= TimeoutSeconds)) Reset();
        if (packet == null) return false;
        if (packet.Kind == 1)
        {
            if (packet.Generation == Guid.Empty || packet.Count < 0 || packet.Count > MaxRows || packet.ServerName == null)
            { Reset(); return false; }
            // The same begin cannot renew a deadline or replay a completed snapshot.
            if (packet.Generation == Generation) return false;
            Reset();
            Generation = packet.Generation;
            BeginCount = packet.Count;
            CanEdit = packet.CanEdit;
            ServerName = packet.ServerName;
            _started = now;
            _collecting = true;
            return false;
        }
        if (!_collecting || packet.Generation != Generation) return false;
        if (packet.Kind == 2)
        {
            if (packet.Row == null || string.IsNullOrEmpty(packet.Row.Id) ||
                _incoming.Count >= BeginCount || _incoming.Count >= MaxRows || !_ids.Add(packet.Row.Id))
            { Reset(); return false; }
            // Own the received data: a caller reusing a mutable packet/record cannot change an
            // already accepted identifier or silently invalidate duplicate detection.
            _incoming.Add(Copy(packet.Row));
            return false;
        }
        if (packet.Kind != 3) return false;
        if (packet.Count != BeginCount || _incoming.Count != BeginCount)
        { Reset(); return false; }
        Rows = Array.AsReadOnly(_incoming.ToArray());
        _incoming.Clear();
        _ids.Clear();
        _collecting = false;
        return true;
    }

    internal void Reset()
    {
        _incoming.Clear();
        _ids.Clear();
        _collecting = false;
        _started = 0;
        Rows = Array.Empty<ScopedSettingRecord>();
        CanEdit = false;
        ServerName = "";
        Generation = Guid.Empty;
        BeginCount = 0;
    }

    private static ScopedSettingRecord Copy(ScopedSettingRecord r) => new()
    {
        Id = r.Id, PluginId = r.PluginId, ModName = r.ModName, Version = r.Version,
        Section = r.Section, Key = r.Key, Help = r.Help, Type = r.Type,
        Default = r.Default, Value = r.Value, Personal = r.Personal, Editable = r.Editable,
        Choices = r.Choices == null ? Array.Empty<string>() : (string[])r.Choices.Clone(),
        Min = r.Min, Max = r.Max, MaxLength = r.MaxLength,
    };
}
