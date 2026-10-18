using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Client side of the PrSt channel without game dependencies: the displayed catalog, value
/// notifications and pending writes. Game thread only; the caller supplies the transport and time.
/// <list type="bullet">
/// <item>A snapshot or delta notifies only rows whose value differs from the one shown before
/// (a row the server provides for the first time counts as changed).</item>
/// <item>Write results are matched by request id, whatever snapshot generation they carry, so an
/// accepted write that crosses a re-snapshot still completes as accepted.</item>
/// <item>A write requested while a new snapshot is arriving waits for it and is then sent under
/// the new generation. A timed-out write whose value the server already shows reports success.</item>
/// <item><see cref="Tick"/> allocates nothing in steady state and clears once per disconnect.</item>
/// </list>
/// </summary>
internal sealed class ScopedSettingsClient
{
    internal const double RequestTimeoutSeconds = 8;
    internal const int MaxPending = 32;

    private readonly Func<byte[], bool> _send;
    private readonly ScopedSettingsSnapshot _receiver = new();
    private readonly Dictionary<string, ScopedSettingRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly List<Pending> _scratch = new();
    private List<ScopedSettingRecord> _rows = new();
    private Guid _generation;
    private bool _waiting;   // some pending write waits for the snapshot in flight

    internal ScopedSettingsClient(Func<byte[], bool> send) => _send = send ?? throw new ArgumentNullException(nameof(send));

    internal IReadOnlyList<ScopedSettingRecord> Rows => _rows;
    internal bool Ready { get; private set; }
    internal bool CanEditServerWide { get; private set; }
    internal string ServerName { get; private set; } = "";
    internal int Revision { get; private set; }
    internal Guid Generation => _generation;
    internal int PendingCount => _pending.Count;
    internal event Action<string, string>? ValueChanged;

    private sealed class Pending : IDisposable
    {
        internal ScopedSettingsClient Owner = null!;
        internal Guid Id;
        internal string RowId = "";
        internal string Value = "";
        internal string Expected = "";
        internal bool Personal;
        internal string Type = "";
        internal bool Queued;
        internal double Deadline;
        internal Action<ServerSettingResult>? Done;
        public void Dispose() { Owner._pending.Remove(Id); Done = null; }
    }

    internal string? Read(string id) => Ready && _byId.TryGetValue(id, out var row) ? row.Value : null;
    internal bool Has(string id, string type) => Ready && _byId.TryGetValue(id, out var row) && row.Personal && row.Type == type;

    internal IDisposable Request(string id, string value, string? expected, Action<ServerSettingResult> done, double now)
    {
        if (done == null) throw new ArgumentNullException(nameof(done));
        var pending = new Pending { Owner = this, Id = Guid.NewGuid(), RowId = id ?? "", Value = value ?? "", Deadline = now + RequestTimeoutSeconds, Done = done };
        string error = "";
        if (!Ready || !_byId.TryGetValue(pending.RowId, out var row)) { error = "This server setting is unavailable."; row = null; }
        else
        {
            if (!row.Editable || !row.Personal && !CanEditServerWide) error = "Only server admins can change this setting.";
            if (_pending.Count >= MaxPending) error = "Too many pending setting changes.";
            foreach (var other in _pending.Values) if (other.RowId == pending.RowId) error = "A change to this setting is already pending.";
        }
        if (error.Length > 0) { done(new(false, Read(pending.RowId) ?? "", error)); return pending; }
        pending.Expected = expected ?? row!.Value;
        pending.Personal = row!.Personal;
        pending.Type = row.Type;
        _pending.Add(pending.Id, pending);
        if (_receiver.Collecting(now)) { pending.Queued = true; _waiting = true; }
        else Send(pending);
        return pending;
    }

    /// <summary>Feed one decoded server packet.</summary>
    internal void Accept(ScopedSettingsPacket packet, double now)
    {
        if (packet == null) return;
        if (packet.Kind <= 3)
        {
            if (!_receiver.Accept(packet, now)) return;
            _previous.Clear();
            foreach (var row in _rows) _previous[row.Id] = row.Value;
            _rows = new(_receiver.Rows);
            _byId.Clear();
            foreach (var row in _rows) _byId[row.Id] = row;
            CanEditServerWide = _receiver.CanEdit; ServerName = _receiver.ServerName;
            _generation = _receiver.Generation;
            Ready = true; Revision++;
            foreach (var row in _rows)
                if (!_previous.TryGetValue(row.Id, out var old) || old != row.Value) Notify(row.Id, row.Value);
            _previous.Clear();
            SendQueued();
            return;
        }
        switch (packet.Kind)
        {
            case 4:
                if (packet.Generation == _generation) SetValue(packet.Id, packet.Value);
                break;
            case 6:
                // Request ids are unique per write and pending writes end on disconnect, so the id
                // alone identifies the answer, also when a re-snapshot changed the generation meanwhile.
                if (!_pending.TryGetValue(packet.RequestId, out var pending) || pending.RowId != packet.Id || pending.Queued) return;
                if (packet.Success || packet.Error.StartsWith("This setting changed on the server.", StringComparison.Ordinal)) SetValue(packet.Id, packet.Value);
                Complete(pending, new(packet.Success, packet.Value, packet.Error));
                break;
        }
    }

    /// <summary>Per frame. Disconnected: forget everything once. Connected: expire overdue writes
    /// and send writes whose awaited snapshot was abandoned. No allocation in steady state.</summary>
    internal void Tick(bool connected, double now)
    {
        if (!connected)
        {
            if (Ready || _rows.Count > 0 || _pending.Count > 0 || _generation != Guid.Empty) Clear("Disconnected from the server.");
            return;
        }
        if (_pending.Count == 0) return;
        if (_waiting && !_receiver.Collecting(now)) SendQueued();
        _scratch.Clear();
        foreach (var pending in _pending.Values) if (now >= pending.Deadline) _scratch.Add(pending);
        if (_scratch.Count == 0) return;
        foreach (var pending in _scratch)
        {
            string shown = Read(pending.RowId) ?? "";
            // The answer was lost, but the server already shows the requested value.
            Complete(pending, !pending.Queued && Ready && _byId.ContainsKey(pending.RowId) && shown == pending.Value
                ? new(true, shown, "")
                : new(false, shown, "The server did not answer in time."));
        }
        _scratch.Clear();
    }

    /// <summary>Forget the catalog and end every pending write with <paramref name="reason"/>.</summary>
    internal void Clear(string reason)
    {
        if (Ready || _rows.Count > 0) Revision++;
        Ready = false; CanEditServerWide = false; ServerName = "";
        _rows = new(); _byId.Clear(); _receiver.Reset();
        _generation = Guid.Empty; _waiting = false;
        if (_pending.Count == 0) return;
        _scratch.Clear();
        _scratch.AddRange(_pending.Values);
        foreach (var pending in _scratch) Complete(pending, new(false, "", reason));
        _scratch.Clear();
    }

    /// <summary>Drop every subscriber (Core unload).</summary>
    internal void ClearSubscribers() => ValueChanged = null;

    private void SendQueued()
    {
        if (!_waiting) return;
        _waiting = false;
        _scratch.Clear();
        foreach (var pending in _pending.Values) if (pending.Queued) _scratch.Add(pending);
        foreach (var pending in _scratch)
        {
            pending.Queued = false;
            if (!Ready || !_byId.TryGetValue(pending.RowId, out var row))
            { Complete(pending, new(false, "", "This setting is no longer published.")); continue; }
            if (row.Personal != pending.Personal || row.Type != pending.Type)
            { Complete(pending, new(false, row.Value, "The setting definition changed. Reopen its page.")); continue; }
            Send(pending);
        }
        _scratch.Clear();
    }

    private void Send(Pending pending)
    {
        try
        {
            byte[] bytes = ScopedSettingsWire.Encode(new ScopedSettingsPacket
            {
                Kind = 5, Generation = _generation, RequestId = pending.Id, Id = pending.RowId, Personal = pending.Personal,
                Type = pending.Type, Expected = pending.Expected, Value = pending.Value,
            });
            if (!_send(bytes)) Complete(pending, new(false, Read(pending.RowId) ?? "", "The connection did not accept the change."));
        }
        catch (Exception e) { Complete(pending, new(false, Read(pending.RowId) ?? "", e.Message)); }
    }

    private void Complete(Pending pending, ServerSettingResult result)
    {
        if (!_pending.ContainsKey(pending.Id)) return;
        var done = pending.Done; pending.Dispose();
        try { done?.Invoke(result); } catch (Exception e) { Warn("Settings completion: " + e.Message); }
    }

    private void SetValue(string id, string value)
    {
        if (!_byId.TryGetValue(id, out var row) || row.Value == value) return;
        row.Value = value; Notify(id, value);
    }

    private void Notify(string id, string value)
    {
        if (ValueChanged == null) return;
        foreach (Action<string, string> callback in ValueChanged.GetInvocationList())
            try { callback(id, value); } catch (Exception e) { Warn("Player settings subscriber: " + e.Message); }
    }

    /// <summary>Warning sink; Core points it at its logger.</summary>
    internal Action<string>? Warning { get; set; }
    private void Warn(string message) => Warning?.Invoke(message);
}
