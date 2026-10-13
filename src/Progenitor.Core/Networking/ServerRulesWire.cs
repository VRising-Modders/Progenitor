using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>Client → server: one Server rules request (<c>get</c> or <c>write</c>).</summary>
internal sealed class ServerRulesRequest
{
    internal const string OpGet = "get";
    internal const string OpWrite = "write";

    /// <summary>WORLDRESTORE1: an admin reads the restore points (answer: <see cref="ServerRulesResponse.Backups"/>).</summary>
    internal const string OpBackups = "backups";

    /// <summary>WORLDRESTORE1: ask for a restore of <see cref="Key"/> (an entry id) at the next restart.</summary>
    internal const string OpRestore = "restore";

    /// <summary>WORLDRESTORE1: delete the pending restore request (and stop a restart-now countdown).</summary>
    internal const string OpRestoreCancel = "restore_cancel";

    /// <summary>WORLDRESTORE1: how many Progenitor backups to keep (<see cref="Value"/>, 0 to 100; 0 keeps all).</summary>
    internal const string OpBackupsKeep = "backups_keep";

    /// <summary>BACKUPNOW1: back up the world now (a manual backup; <see cref="DryRun"/> checks only).</summary>
    internal const string OpBackupNow = "backup_now";

    /// <summary>BACKUPNOW1: store one backup setting: <see cref="Key"/> (<c>BackupAtStart</c>, <c>BackupEveryHours</c>, <c>BackupKeepCount</c>, <c>BackupKeepDays</c>) and <see cref="Value"/>.</summary>
    internal const string OpBackupsSet = "backups_set";

    public int Nonce { get; set; }
    public string Op { get; set; } = OpGet;
    public string Key { get; set; } = "";

    /// <summary>The value the page showed (the next-start value); a write whose value changed meanwhile is refused.</summary>
    public string Expected { get; set; } = "";

    public bool HasExpected { get; set; }
    public string Value { get; set; } = "";

    /// <summary>
    /// The admin confirmed a change that may reset the world (wipes turned on, or a reset date already reached). The
    /// server refuses such a write without it.
    /// </summary>
    public bool Confirm { get; set; }

    /// <summary>WORLDRESTORE1 <c>restore</c>: the wipe choice (<c>none</c>, <c>clock-today</c>, <c>wipes-off</c>, <c>date</c>; the date in <see cref="Value"/>).</summary>
    public string Wipe { get; set; } = "";

    /// <summary>WORLDRESTORE1 <c>restore</c>: the entry's shape as the admin saw it (a changed restore point is refused).</summary>
    public string Shape { get; set; } = "";

    /// <summary>WORLDRESTORE1 <c>restore</c>: stop the server after the countdown (the host must restart it).</summary>
    public bool RestartNow { get; set; }

    /// <summary>WORLDRESTORE1 <c>restore</c>: check and plan only; nothing is written.</summary>
    public bool DryRun { get; set; }
}

/// <summary>Server → client: the answer to one request.</summary>
internal sealed class ServerRulesResponse
{
    public int Nonce { get; set; }
    public string Op { get; set; } = "";
    public string Key { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Note { get; set; } = "";
    public ServerRulesState? State { get; set; }

    /// <summary>WORLDRESTORE1: the restore points, for an admin's <c>backups</c>, <c>restore</c>, <c>restore_cancel</c> and <c>backups_keep</c>.</summary>
    public ServerBackupsState? Backups { get; set; }
}

/// <summary>
/// The Server rules channel's packet (<c>'PrSv'</c>, <see cref="CustomEventPolicy.ServerRulesEventId"/>, one id both ways): a
/// version byte, a kind byte (request, answer, changed), the nonce, then UTF-8 JSON, at most 15 KiB. A password is never on
/// the wire from the server: the state says only whether one is set.
/// </summary>
internal static class ServerRulesWire
{
    internal const byte Version = 1;
    internal const byte KindRequest = 0;
    internal const byte KindResponse = 1;
    internal const byte KindChanged = 2;

    /// <summary>
    /// BACKUPSDLG1, server → admins who read the Backups: the pending restore request or its countdown changed (written,
    /// cancelled, a countdown that ended without a stop). No state travels: a client with a Backups dialog open asks again.
    /// Older clients drop the unknown kind.
    /// </summary>
    internal const byte KindBackupsChanged = 3;
    internal const int MaxPacketBytes = 15 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static byte[] EncodeRequest(ServerRulesRequest r) => Frame(KindRequest, r.Nonce, w =>
    {
        w.WriteString("op", r.Op ?? "");
        if ((r.Key ?? "").Length > 0) w.WriteString("key", r.Key);
        if (r.HasExpected) w.WriteString("expected", r.Expected ?? "");
        if (r.Op == ServerRulesRequest.OpWrite) w.WriteString("value", r.Value ?? "");
        if (r.Confirm) w.WriteBoolean("confirm", true);
        if (r.Op != ServerRulesRequest.OpWrite && (r.Value ?? "").Length > 0) w.WriteString("value", r.Value);
        if ((r.Wipe ?? "").Length > 0) w.WriteString("wipe", r.Wipe);
        if ((r.Shape ?? "").Length > 0) w.WriteString("shape", r.Shape);
        if (r.RestartNow) w.WriteBoolean("restartNow", true);
        if (r.DryRun) w.WriteBoolean("dryRun", true);
    });

    internal static byte[] EncodeResponse(ServerRulesResponse r) => Frame(KindResponse, r.Nonce, w =>
    {
        w.WriteString("op", r.Op ?? "");
        if ((r.Key ?? "").Length > 0) w.WriteString("key", r.Key);
        w.WriteBoolean("ok", r.Ok);
        if ((r.Error ?? "").Length > 0) w.WriteString("error", r.Error);
        if ((r.Note ?? "").Length > 0) w.WriteString("note", r.Note);
        if (r.State != null) WriteState(w, r.State);
        if (r.Backups != null) WriteBackups(w, r.Backups);
    });

    internal static byte[] EncodeChanged(ServerRulesState state) => Frame(KindChanged, 0, w => WriteState(w, state));

    /// <summary>BACKUPSDLG1: the pending request's id now (empty: none), whether a countdown runs, and why it changed.</summary>
    internal static byte[] EncodeBackupsChanged(string pendingId, bool countdown, string why) => Frame(KindBackupsChanged, 0, w =>
    {
        w.WriteString("pendingId", pendingId ?? "");
        w.WriteBoolean("countdown", countdown);
        w.WriteString("why", why ?? "");
    });

    internal static bool TryDecodeBackupsChanged(ReadOnlySpan<byte> packet, out string pendingId, out bool countdown, out string why)
    {
        pendingId = why = "";
        countdown = false;
        if (KindOf(packet) != KindBackupsChanged || !TryBody(packet, out _, out var doc) || doc == null) return false;
        using (doc)
        {
            var root = doc.RootElement;
            pendingId = Str(root, "pendingId");
            countdown = Bool(root, "countdown");
            why = Str(root, "why");
            return true;
        }
    }

    private static void WriteState(Utf8JsonWriter w, ServerRulesState s)
    {
        w.WriteStartObject("state");
        w.WriteStartArray("rows");
        foreach (var r in s.Rows)
        {
            w.WriteStartObject();
            w.WriteString("key", r.Key);
            if (r.Loaded != null) w.WriteString("loaded", r.Loaded);
            if (r.Next != null) w.WriteString("next", r.Next);
            w.WriteBoolean("pending", r.Pending);
            if (r.Override.Length > 0) w.WriteString("override", r.Override);
            w.WriteBoolean("editable", r.Editable);
            if (r.Reason.Length > 0) w.WriteString("reason", r.Reason);
            if (r.LockedBy.Length > 0) w.WriteString("lockedBy", r.LockedBy);
            if (r.Source.Length > 0) w.WriteString("source", r.Source);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteBoolean("passwordSet", s.PasswordSet);
        w.WriteBoolean("passwordNextSet", s.PasswordNextSet);
        w.WriteNumber("restartPending", s.RestartPending);
        w.WriteBoolean("mercilessNow", s.MercilessNow);
        w.WriteBoolean("mercilessNext", s.MercilessNext);
        w.WriteString("territory", s.Territory);
        if (s.DaysRunning is int days) w.WriteNumber("daysRunning", days);
        w.WriteString("startDate", s.StartDate);
        w.WriteString("saveName", s.SaveName);
        if (s.StandardNow is bool std) w.WriteBoolean("standardNow", std);
        w.WriteBoolean("standardMayChange", s.StandardMayChange);
        w.WriteString("nextReset", s.NextReset);
        w.WriteString("presetWarning", s.PresetWarning);
        w.WriteNumber("adminReserve", s.AdminReserve);
        w.WriteNumber("maxPlayersMax", s.MaxPlayersMax);
        w.WriteEndObject();
    }

    private static void WriteBackups(Utf8JsonWriter w, ServerBackupsState b)
    {
        w.WriteStartObject("backups");
        w.WriteStartArray("entries");
        foreach (var e in b.Entries)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("kind", e.Kind);
            w.WriteString("kindLabel", e.KindLabel);
            w.WriteString("name", e.Name);
            w.WriteString("label", e.Label);
            w.WriteString("created", e.Created);
            if (e.StartDate.Length > 0) w.WriteString("startDate", e.StartDate);
            if (e.AgeDays is int age) w.WriteNumber("ageDays", age);
            w.WriteNumber("size", e.Size);
            w.WriteBoolean("restorable", e.Restorable);
            if (e.Reason.Length > 0) w.WriteString("reason", e.Reason);
            w.WriteString("shape", e.Shape);
            if (e.BackupKind.Length > 0) w.WriteString("bkind", e.BackupKind);
            if (e.Trigger.Length > 0) w.WriteString("trigger", e.Trigger);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteNumber("more", b.More);
        w.WriteNumber("count", b.Count);
        w.WriteNumber("archiveBytes", b.ArchiveBytes);
        w.WriteNumber("backupBytes", b.BackupBytes);
        w.WriteNumber("currentBytes", b.CurrentBytes);
        w.WriteNumber("freeBytes", b.FreeBytes);
        w.WriteNumber("keep", b.Keep);
        if (b.KeepLockedBy.Length > 0) w.WriteString("keepLockedBy", b.KeepLockedBy);
        w.WriteBoolean("restoreAllowed", b.RestoreAllowed);
        if (b.RestoreRefusal.Length > 0) w.WriteString("restoreRefusal", b.RestoreRefusal);
        w.WriteNumber("nextInterval", b.NextInterval);
        w.WriteString("nextDay", b.NextDay);
        w.WriteBoolean("intervalWritable", b.IntervalWritable);
        if (b.IntervalReason.Length > 0) w.WriteString("intervalReason", b.IntervalReason);
        w.WriteString("serverNow", b.ServerNow);
        w.WriteNumber("online", b.Online);
        if (b.PendingId.Length > 0)
        {
            w.WriteStartObject("pending");
            w.WriteString("id", b.PendingId);
            w.WriteString("name", b.PendingName);
            w.WriteString("requestedUtc", b.PendingRequestedUtc);
            w.WriteString("wipe", b.PendingWipe);
            if (b.PendingWipeDate.Length > 0) w.WriteString("wipeDate", b.PendingWipeDate);
            w.WriteBoolean("restartNow", b.PendingRestartNow);
            w.WriteEndObject();
        }

        w.WriteNumber("countdown", b.Countdown);
        if (b.SaveName.Length > 0) w.WriteString("saveName", b.SaveName);
        w.WriteStartArray("pruneNext");
        foreach (var e in b.PruneNext)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("name", e.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        // BACKUPNOW1
        w.WriteStartObject("made");
        w.WriteNumber("manualBytes", b.ManualBytes);
        w.WriteNumber("scheduledBytes", b.ScheduledBytes);
        if (b.PreUpdateBytes > 0) w.WriteNumber("preUpdateBytes", b.PreUpdateBytes);
        w.WriteString("where", b.BackupsWhere);
        w.WriteBoolean("allowed", b.BackupAllowed);
        if (b.BackupRefusal.Length > 0) w.WriteString("refusal", b.BackupRefusal);
        if (b.BackupNowLockedBy.Length > 0) w.WriteString("nowLockedBy", b.BackupNowLockedBy);
        w.WriteBoolean("atStart", b.AtStart);
        w.WriteNumber("everyHours", b.EveryHours);
        w.WriteNumber("keepCount", b.KeepCount);
        w.WriteNumber("keepDays", b.KeepDays);
        if (b.AtStartLockedBy.Length > 0) w.WriteString("atStartLockedBy", b.AtStartLockedBy);
        if (b.EveryHoursLockedBy.Length > 0) w.WriteString("everyHoursLockedBy", b.EveryHoursLockedBy);
        if (b.KeepCountLockedBy.Length > 0) w.WriteString("keepCountLockedBy", b.KeepCountLockedBy);
        if (b.KeepDaysLockedBy.Length > 0) w.WriteString("keepDaysLockedBy", b.KeepDaysLockedBy);
        if (b.NextScheduled.Length > 0) w.WriteString("next", b.NextScheduled);
        if (b.JobRunning)
        {
            w.WriteStartObject("job");
            w.WriteString("kind", b.JobKind);
            w.WriteString("by", b.JobBy);
            w.WriteString("phase", b.JobPhase);
            w.WriteEndObject();
        }

        if (b.LastBackupSummary.Length > 0)
        {
            w.WriteStartObject("last");
            w.WriteBoolean("ok", b.LastBackupOk);
            w.WriteString("summary", b.LastBackupSummary);
            w.WriteString("finished", b.LastBackupFinished);
            w.WriteEndObject();
        }

        if (b.ScheduleFailure.Length > 0)
        {
            w.WriteString("scheduleFailure", b.ScheduleFailure);
            w.WriteString("scheduleFailureAt", b.ScheduleFailureAt);
        }

        w.WriteStartArray("pruneNext");
        foreach (var e in b.PruneNextBackup)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("name", e.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (b.AutosaveKnown)
        {
            w.WriteStartObject("autosaves");
            w.WriteNumber("count", b.AutosaveCount);
            w.WriteNumber("bytes", b.AutosaveBytes);
            w.WriteString("newest", b.AutosaveNewest);
            w.WriteString("oldest", b.AutosaveOldest);
            w.WriteNumber("keep", b.AutosaveKeep);
            w.WriteNumber("every", b.AutosaveEvery);
            w.WriteEndObject();
        }

        w.WriteEndObject();
        if (b.ResultId.Length > 0)
        {
            w.WriteStartObject("result");
            w.WriteString("id", b.ResultId);
            w.WriteBoolean("ok", b.ResultOk);
            w.WriteString("summary", b.ResultSummary);
            w.WriteString("finishedUtc", b.ResultFinishedUtc);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static ServerBackupsState? ReadBackups(JsonElement root)
    {
        if (!root.TryGetProperty("backups", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        var s = new ServerBackupsState();
        if (b.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in entries.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object || s.Entries.Count >= ServerBackups.MaxEntries) continue;
                s.Entries.Add(new ServerBackupView
                {
                    Id = Str(x, "id"), Kind = Str(x, "kind"), KindLabel = Str(x, "kindLabel"), Name = Str(x, "name"), Label = Str(x, "label"),
                    Created = Str(x, "created"), StartDate = Str(x, "startDate"), AgeDays = Int(x, "ageDays"),
                    Size = Long(x, "size") ?? 0, Restorable = Bool(x, "restorable"), Reason = Str(x, "reason"), Shape = Str(x, "shape"),
                    BackupKind = Str(x, "bkind"), Trigger = Str(x, "trigger"),
                });
            }
        }

        s.More = Int(b, "more") ?? 0;
        s.Count = Int(b, "count") ?? s.Entries.Count;
        s.ArchiveBytes = Long(b, "archiveBytes") ?? 0;
        s.BackupBytes = Long(b, "backupBytes") ?? 0;
        s.CurrentBytes = Long(b, "currentBytes") ?? 0;
        s.FreeBytes = Long(b, "freeBytes") ?? -1;
        s.Keep = Int(b, "keep") ?? Progenitor.WorldRestore.WorldBackupCatalog.DefaultKeep;
        s.KeepLockedBy = Str(b, "keepLockedBy");
        s.RestoreAllowed = Bool(b, "restoreAllowed");
        s.RestoreRefusal = Str(b, "restoreRefusal");
        s.NextInterval = Int(b, "nextInterval") ?? 0;
        s.NextDay = Str(b, "nextDay");
        s.IntervalWritable = Bool(b, "intervalWritable");
        s.IntervalReason = Str(b, "intervalReason");
        s.ServerNow = Str(b, "serverNow");
        s.Online = Int(b, "online") ?? 0;
        if (b.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            s.PendingId = Str(p, "id");
            s.PendingName = Str(p, "name");
            s.PendingRequestedUtc = Str(p, "requestedUtc");
            s.PendingWipe = Str(p, "wipe");
            s.PendingWipeDate = Str(p, "wipeDate");
            s.PendingRestartNow = Bool(p, "restartNow");
        }

        s.Countdown = Int(b, "countdown") ?? -1;
        s.SaveName = Str(b, "saveName");
        if (b.TryGetProperty("pruneNext", out var prune) && prune.ValueKind == JsonValueKind.Array)
            foreach (var x in prune.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && s.PruneNext.Count < ServerBackups.MaxEntries)
                    s.PruneNext.Add(new ServerBackupView { Id = Str(x, "id"), Name = Str(x, "name") });
        if (b.TryGetProperty("made", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            s.ManualBytes = Long(m, "manualBytes") ?? 0;
            s.ScheduledBytes = Long(m, "scheduledBytes") ?? 0;
            s.PreUpdateBytes = Long(m, "preUpdateBytes") ?? 0;
            s.BackupsWhere = Str(m, "where");
            s.BackupAllowed = Bool(m, "allowed");
            s.BackupRefusal = Str(m, "refusal");
            s.BackupNowLockedBy = Str(m, "nowLockedBy");
            s.AtStart = Bool(m, "atStart");
            s.EveryHours = Int(m, "everyHours") ?? 0;
            s.KeepCount = Int(m, "keepCount") ?? Progenitor.WorldRestore.BackupPolicy.DefaultCount;
            s.KeepDays = Int(m, "keepDays") ?? Progenitor.WorldRestore.BackupPolicy.DefaultDays;
            s.AtStartLockedBy = Str(m, "atStartLockedBy");
            s.EveryHoursLockedBy = Str(m, "everyHoursLockedBy");
            s.KeepCountLockedBy = Str(m, "keepCountLockedBy");
            s.KeepDaysLockedBy = Str(m, "keepDaysLockedBy");
            s.NextScheduled = Str(m, "next");
            if (m.TryGetProperty("job", out var j) && j.ValueKind == JsonValueKind.Object)
            {
                s.JobRunning = true;
                s.JobKind = Str(j, "kind");
                s.JobBy = Str(j, "by");
                s.JobPhase = Str(j, "phase");
            }

            if (m.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                s.LastBackupOk = Bool(l, "ok");
                s.LastBackupSummary = Str(l, "summary");
                s.LastBackupFinished = Str(l, "finished");
            }

            s.ScheduleFailure = Str(m, "scheduleFailure");
            s.ScheduleFailureAt = Str(m, "scheduleFailureAt");
            if (m.TryGetProperty("pruneNext", out var pn) && pn.ValueKind == JsonValueKind.Array)
                foreach (var x in pn.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Object && s.PruneNextBackup.Count < ServerBackups.MaxEntries)
                        s.PruneNextBackup.Add(new ServerBackupView { Id = Str(x, "id"), Name = Str(x, "name") });
            if (m.TryGetProperty("autosaves", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                s.AutosaveKnown = true;
                s.AutosaveCount = Int(a, "count") ?? 0;
                s.AutosaveBytes = Long(a, "bytes") ?? 0;
                s.AutosaveNewest = Str(a, "newest");
                s.AutosaveOldest = Str(a, "oldest");
                s.AutosaveKeep = Int(a, "keep") ?? -1;
                s.AutosaveEvery = Int(a, "every") ?? -1;
            }
        }

        if (b.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            s.ResultId = Str(r, "id");
            s.ResultOk = Bool(r, "ok");
            s.ResultSummary = Str(r, "summary");
            s.ResultFinishedUtc = Str(r, "finishedUtc");
        }

        return s;
    }

    private static long? Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n) ? n : null;

    private static byte[] Frame(byte kind, int nonce, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(Version);
        stream.WriteByte(kind);
        var n = BitConverter.GetBytes(nonce);
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        stream.Write(n, 0, 4);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The kind byte of a packet, or -1 when it is not one of ours.</summary>
    internal static int KindOf(ReadOnlySpan<byte> packet) =>
        packet.Length >= 6 && packet.Length <= MaxPacketBytes && packet[0] == Version && packet[1] <= KindBackupsChanged ? packet[1] : -1;

    internal static bool TryDecodeRequest(ReadOnlySpan<byte> packet, out ServerRulesRequest request)
    {
        request = new ServerRulesRequest();
        if (KindOf(packet) != KindRequest || !TryBody(packet, out int nonce, out var doc) || doc == null) return false;
        using (doc)
        {
            var root = doc.RootElement;
            request.Nonce = nonce;
            request.Op = Str(root, "op");
            request.Key = Str(root, "key");
            request.HasExpected = root.TryGetProperty("expected", out var e) && e.ValueKind == JsonValueKind.String;
            request.Expected = request.HasExpected ? e.GetString() ?? "" : "";
            // The value is kept as sent (a password's spaces count; the server decides).
            request.Value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            request.Confirm = root.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;
            request.Wipe = Str(root, "wipe");
            request.Shape = Str(root, "shape");
            request.RestartNow = Bool(root, "restartNow");
            request.DryRun = Bool(root, "dryRun");
            return request.Op.Length > 0;
        }
    }

    internal static bool TryDecodeResponse(ReadOnlySpan<byte> packet, out ServerRulesResponse response)
    {
        response = new ServerRulesResponse();
        if (KindOf(packet) != KindResponse || !TryBody(packet, out int nonce, out var doc) || doc == null) return false;
        using (doc)
        {
            var root = doc.RootElement;
            response.Nonce = nonce;
            response.Op = Str(root, "op");
            response.Key = Str(root, "key");
            response.Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            response.Error = Str(root, "error");
            response.Note = Str(root, "note");
            response.State = ReadState(root);
            response.Backups = ReadBackups(root);
            return true;
        }
    }

    internal static bool TryDecodeChanged(ReadOnlySpan<byte> packet, out ServerRulesState? state)
    {
        state = null;
        if (KindOf(packet) != KindChanged || !TryBody(packet, out _, out var doc) || doc == null) return false;
        using (doc)
        {
            state = ReadState(doc.RootElement);
            return state != null;
        }
    }

    private static ServerRulesState? ReadState(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var st) || st.ValueKind != JsonValueKind.Object) return null;
        var s = new ServerRulesState();
        if (st.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in rows.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object || s.Rows.Count >= 64) continue;
                s.Rows.Add(new ServerRuleRow
                {
                    Key = Str(x, "key"),
                    Loaded = x.TryGetProperty("loaded", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                    Next = x.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    Pending = Bool(x, "pending"),
                    Override = Str(x, "override"),
                    Editable = Bool(x, "editable"),
                    Reason = Str(x, "reason"),
                    LockedBy = Str(x, "lockedBy"),
                    Source = Str(x, "source"),
                });
            }
        }

        s.PasswordSet = Bool(st, "passwordSet");
        s.PasswordNextSet = Bool(st, "passwordNextSet");
        s.RestartPending = Int(st, "restartPending") ?? 0;
        s.MercilessNow = Bool(st, "mercilessNow");
        s.MercilessNext = Bool(st, "mercilessNext");
        s.Territory = Str(st, "territory");
        s.DaysRunning = Int(st, "daysRunning");
        s.StartDate = Str(st, "startDate");
        s.SaveName = Str(st, "saveName");
        s.StandardNow = st.TryGetProperty("standardNow", out var sn) && sn.ValueKind is JsonValueKind.True or JsonValueKind.False ? sn.ValueKind == JsonValueKind.True : null;
        s.StandardMayChange = Bool(st, "standardMayChange");
        s.NextReset = Str(st, "nextReset");
        s.PresetWarning = Str(st, "presetWarning");
        s.AdminReserve = Int(st, "adminReserve") ?? 0;
        s.MaxPlayersMax = Int(st, "maxPlayersMax") ?? ServerRules.MaxSlots;
        return s;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";

    private static bool Bool(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n) ? n : null;

    private static bool TryBody(ReadOnlySpan<byte> packet, out int nonce, out JsonDocument? doc)
    {
        doc = null;
        var n = packet.Slice(2, 4).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        nonce = BitConverter.ToInt32(n, 0);
        try
        {
            string json = StrictUtf8.GetString(packet.Slice(6).ToArray());
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return true;
            doc.Dispose();
            doc = null;
            return false;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            doc?.Dispose();
            doc = null;
            return false;
        }
    }
}
