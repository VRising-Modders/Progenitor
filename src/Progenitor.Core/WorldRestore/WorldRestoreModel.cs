using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.WorldRestore;

/// <summary>What the restored world's scheduled wipe does (the admin's choice when its wipe date has passed).</summary>
internal static class WipeModes
{
    /// <summary>Nothing changes: the wipe date is in the future (or wipes are off).</summary>
    internal const string None = "none";

    /// <summary>The restored <c>StartDate.json</c> is rewritten to the day of the restore (the original is kept beside it).</summary>
    internal const string ClockToday = "clock-today";

    /// <summary><c>ResetDaysInterval</c> 0 in the next-start host settings.</summary>
    internal const string WipesOff = "wipes-off";

    /// <summary>A new wipe date: <c>ResetDaysInterval</c> becomes the days from the restored start to it (and <c>DayOfReset</c> Any when needed).</summary>
    internal const string Date = "date";

    internal static bool IsKnown(string? mode) => mode is None or ClockToday or WipesOff or Date;
}

/// <summary>
/// WORLDRESTORE1: <c>BepInEx\progenitor-restore.json</c>, written by Core on the server (an admin's request) and applied by
/// Bootstrap at the next server start, before the game picks its save (spec §3.2).
/// </summary>
internal sealed class RestoreRequest
{
    internal const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string RequestId { get; set; } = "";
    public DateTime RequestedUtc { get; set; }

    /// <summary>The admin's character name: only in the server's own file and log.</summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>The live save folder, absolute (<c>&lt;persist&gt;\Saves\v&lt;N&gt;\&lt;save&gt;</c>).</summary>
    public string SaveFolder { get; set; } = "";

    public string SaveName { get; set; } = "";

    /// <summary><c>archive</c> (a game wipe archive) or <c>backup</c> (a Progenitor backup).</summary>
    public string SourceKind { get; set; } = "";

    public string SourcePath { get; set; } = "";

    /// <summary>How the source reads to a person (<c>Wipe archive from Wed, Oct 1, 2026</c>).</summary>
    public string SourceName { get; set; } = "";

    /// <summary>The source's <see cref="WorldManifest.ShapeHash"/> when the admin confirmed: a source that changed since is refused.</summary>
    public string ExpectShape { get; set; } = "";

    /// <summary>The source's <c>StartDate.json</c> date (<c>yyyy-MM-dd</c>), or empty.</summary>
    public string ExpectStartDate { get; set; } = "";

    public string WipeMode { get; set; } = WipeModes.None;

    /// <summary><see cref="WipeModes.WipesOff"/> (0) and <see cref="WipeModes.Date"/>: the <c>ResetDaysInterval</c> to write.</summary>
    public int WipeIntervalDays { get; set; }

    /// <summary><see cref="WipeModes.Date"/>: <c>Any</c> when the weekday must not move the date; empty: leave <c>DayOfReset</c>.</summary>
    public string WipeDayOfReset { get; set; } = "";

    /// <summary>The host settings file the next start reads <c>ResetDaysInterval</c> from (Server rules' write target).</summary>
    public string HostSettingsPath { get; set; } = "";

    /// <summary>The picked wipe date (<c>yyyy-MM-dd</c>) for the record.</summary>
    public string WipeDate { get; set; } = "";

    /// <summary>
    /// WORLDRESTORE2 (M4): the host settings files the next start reads, lowest first (the install's default, the
    /// persistent-data copy, the save folder's), so Bootstrap recomputes the wipe date at apply time.
    /// </summary>
    public List<string> WipeLayers { get; set; } = new();

    /// <summary>Keep the newest this many Progenitor backups after the restore (0: keep all).</summary>
    public int KeepBackups { get; set; } = WorldBackupCatalog.DefaultKeep;

    public bool RestartNow { get; set; }

    internal byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("requestId", RequestId);
            w.WriteString("requestedUtc", WorldJson.Utc(RequestedUtc));
            w.WriteString("requestedBy", RequestedBy);
            w.WriteString("saveFolder", SaveFolder);
            w.WriteString("saveName", SaveName);
            w.WriteStartObject("source");
            w.WriteString("kind", SourceKind);
            w.WriteString("path", SourcePath);
            w.WriteString("name", SourceName);
            w.WriteEndObject();
            w.WriteStartObject("expect");
            w.WriteString("shape", ExpectShape);
            w.WriteString("startDate", ExpectStartDate);
            w.WriteEndObject();
            w.WriteStartObject("wipe");
            w.WriteString("mode", WipeMode);
            w.WriteNumber("intervalDays", WipeIntervalDays);
            w.WriteString("dayOfReset", WipeDayOfReset);
            w.WriteString("hostSettingsPath", HostSettingsPath);
            w.WriteString("date", WipeDate);
            w.WriteStartArray("layers");
            foreach (string l in WipeLayers) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteNumber("keepBackups", KeepBackups);
            w.WriteBoolean("restartNow", RestartNow);
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>Parse a request; null with <paramref name="error"/> when it is not one.</summary>
    internal static RestoreRequest? Parse(byte[]? bytes, out string error)
    {
        error = "";
        if (bytes == null || bytes.Length == 0)
        {
            error = "the request file is empty";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(bytes), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the request is not a JSON object";
                return null;
            }

            var r = new RestoreRequest
            {
                Schema = WorldJson.Int(root, "schema") ?? 0,
                RequestId = WorldJson.Str(root, "requestId"),
                RequestedUtc = WorldJson.Time(root, "requestedUtc") ?? default,
                RequestedBy = WorldJson.Str(root, "requestedBy"),
                SaveFolder = WorldJson.Str(root, "saveFolder"),
                SaveName = WorldJson.Str(root, "saveName"),
                KeepBackups = WorldJson.Int(root, "keepBackups") ?? WorldBackupCatalog.DefaultKeep,
                RestartNow = WorldJson.Bool(root, "restartNow"),
            };
            if (root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                r.SourceKind = WorldJson.Str(s, "kind");
                r.SourcePath = WorldJson.Str(s, "path");
                r.SourceName = WorldJson.Str(s, "name");
            }

            if (root.TryGetProperty("expect", out var x) && x.ValueKind == JsonValueKind.Object)
            {
                r.ExpectShape = WorldJson.Str(x, "shape");
                r.ExpectStartDate = WorldJson.Str(x, "startDate");
            }

            if (root.TryGetProperty("wipe", out var wp) && wp.ValueKind == JsonValueKind.Object)
            {
                r.WipeMode = WorldJson.Str(wp, "mode");
                r.WipeIntervalDays = WorldJson.Int(wp, "intervalDays") ?? 0;
                r.WipeDayOfReset = WorldJson.Str(wp, "dayOfReset");
                r.HostSettingsPath = WorldJson.Str(wp, "hostSettingsPath");
                r.WipeDate = WorldJson.Str(wp, "date");
                WorldJson.Strings(wp, "layers", r.WipeLayers);
            }

            if (r.WipeMode.Length == 0) r.WipeMode = WipeModes.None;
            if (r.Schema != CurrentSchema)
            {
                error = "unknown request schema " + r.Schema;
                return null;
            }

            if (r.RequestId.Length == 0)
            {
                error = "the request has no id";
                return null;
            }

            return r;
        }
        catch (JsonException e)
        {
            error = "the request is not valid JSON (" + e.Message + ")";
            return null;
        }
    }
}

/// <summary>One step of an apply, as the result records it.</summary>
internal sealed class RestoreStep
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
    public long Ms { get; set; }
}

/// <summary>WORLDRESTORE1: <c>BepInEx\progenitor-restore.done.json</c>, what the last apply did (Core shows it).</summary>
internal sealed class RestoreResult
{
    public int Schema { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public bool Ok { get; set; }

    /// <summary>WORLDRESTORE2: the save folder is missing and could not be put back; the server was stopped (fail closed).</summary>
    public bool Stranded { get; set; }

    /// <summary>One sentence: "Restored …" or "Not restored: … Nothing changed."</summary>
    public string Summary { get; set; } = "";

    /// <summary>Why it was refused (empty on success).</summary>
    public string Reason { get; set; } = "";

    public string SourceName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string BackupName { get; set; } = "";
    public string WipeMode { get; set; } = WipeModes.None;
    public string WipeApplied { get; set; } = "";

    /// <summary>Warnings after the swap (the restore stands): a leftover folder, a backup that could not be pruned.</summary>
    public List<string> Warnings { get; } = new();

    public List<string> Pruned { get; } = new();
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public List<RestoreStep> Steps { get; } = new();

    /// <summary>
    /// BACKUPNOW1 (not in the file): the request ran its steps at this start (not a result reported again, not one the recovery
    /// pass finished). A successful one made the backup of the pre-restore world, so the scheduled start backup is not copied
    /// a second time.
    /// </summary>
    internal bool RanThisStart { get; set; }

    /// <summary>" in 561 ms", or empty when either time is unknown or the span is not a real one.</summary>
    internal string Elapsed()
    {
        if (StartedUtc == default || FinishedUtc == default || FinishedUtc < StartedUtc) return "";
        double ms = (FinishedUtc - StartedUtc).TotalMilliseconds;
        return ms > 24 * 3600 * 1000.0 ? "" : " in " + ((long)ms).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
    }

    internal byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("requestId", RequestId);
            w.WriteBoolean("ok", Ok);
            w.WriteBoolean("stranded", Stranded);
            w.WriteString("summary", Summary);
            w.WriteString("reason", Reason);
            w.WriteString("sourceName", SourceName);
            w.WriteString("saveFolder", SaveFolder);
            w.WriteString("backupPath", BackupPath);
            w.WriteString("backupName", BackupName);
            w.WriteString("wipeMode", WipeMode);
            w.WriteString("wipeApplied", WipeApplied);
            w.WriteStartArray("warnings");
            foreach (string s in Warnings) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteStartArray("pruned");
            foreach (string s in Pruned) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteString("startedUtc", WorldJson.Utc(StartedUtc));
            w.WriteString("finishedUtc", WorldJson.Utc(FinishedUtc));
            w.WriteStartArray("steps");
            foreach (var s in Steps)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteBoolean("ok", s.Ok);
                w.WriteString("detail", s.Detail);
                w.WriteNumber("ms", s.Ms);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    internal static RestoreResult? Parse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(bytes), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var r = new RestoreResult
            {
                Schema = WorldJson.Int(root, "schema") ?? 1,
                RequestId = WorldJson.Str(root, "requestId"),
                Ok = WorldJson.Bool(root, "ok"),
                Stranded = WorldJson.Bool(root, "stranded"),
                Summary = WorldJson.Str(root, "summary"),
                Reason = WorldJson.Str(root, "reason"),
                SourceName = WorldJson.Str(root, "sourceName"),
                SaveFolder = WorldJson.Str(root, "saveFolder"),
                BackupPath = WorldJson.Str(root, "backupPath"),
                BackupName = WorldJson.Str(root, "backupName"),
                WipeMode = WorldJson.Str(root, "wipeMode"),
                WipeApplied = WorldJson.Str(root, "wipeApplied"),
                StartedUtc = WorldJson.Time(root, "startedUtc") ?? default,
                FinishedUtc = WorldJson.Time(root, "finishedUtc") ?? default,
            };
            WorldJson.Strings(root, "warnings", r.Warnings);
            WorldJson.Strings(root, "pruned", r.Pruned);
            if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                foreach (var s in steps.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object)
                        r.Steps.Add(new RestoreStep
                        {
                            Name = WorldJson.Str(s, "name"), Ok = WorldJson.Bool(s, "ok"), Detail = WorldJson.Str(s, "detail"),
                            Ms = WorldJson.Long(s, "ms") ?? 0,
                        });
            return r;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>WORLDRESTORE1: <c>progenitor-backup.json</c> in every Progenitor backup (spec §1.6).</summary>
internal sealed class BackupSidecar
{
    public int Schema { get; set; } = 1;
    public string Kind { get; set; } = WorldRestorePaths.KindBeforeRestore;
    public string SourceSaveName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string PersistenceVersion { get; set; } = "";
    public string ProgenitorVersion { get; set; } = "";

    /// <summary>The backed-up world's <c>StartDate.json</c> (<c>yyyy-MM-dd</c>), or empty.</summary>
    public string StartDate { get; set; } = "";

    /// <summary>What was restored over this world (the source's display name).</summary>
    public string RestoreOf { get; set; } = "";

    public string RequestId { get; set; } = "";

    /// <summary>
    /// WORLDRESTORE2 (M1): the restore chain this backup belongs to: every restore of a save continues the chain of the newest
    /// backup already there, and the first one starts it (its own folder name). Retention never deletes a chain's oldest
    /// backup: the world as it was before the first restore.
    /// </summary>
    public string ChainId { get; set; } = "";

    /// <summary>A second copy kept because the world changed between the backup and the swap (should not happen).</summary>
    public string Note { get; set; } = "";

    /// <summary>BACKUPNOW1: what made a manual or scheduled backup (<c>manual</c>, <c>start</c>, <c>interval</c>), or empty.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>BACKUPNOW1: how many copies it took to get one that matched the save folder before and after (1 normally).</summary>
    public int Attempts { get; set; }

    /// <summary>AUTOUPDATE3: a pre-update backup: the updates it came before (<c>ChatSkin 1.3.0, BroaderMap 2.0.2</c>), or empty.</summary>
    public string Updates { get; set; } = "";

    public List<WorldManifestEntry> Files { get; } = new();

    internal byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("kind", Kind);
            w.WriteString("sourceSaveName", SourceSaveName);
            w.WriteString("createdUtc", WorldJson.Utc(CreatedUtc));
            w.WriteString("persistenceVersion", PersistenceVersion);
            w.WriteString("progenitorVersion", ProgenitorVersion);
            w.WriteString("startDate", StartDate);
            w.WriteString("restoreOf", RestoreOf);
            w.WriteString("requestId", RequestId);
            w.WriteString("chainId", ChainId);
            if (Note.Length > 0) w.WriteString("note", Note);
            if (Trigger.Length > 0) w.WriteString("trigger", Trigger);
            if (Attempts > 0) w.WriteNumber("attempts", Attempts);
            if (Updates.Length > 0) w.WriteString("updates", Updates);
            w.WriteStartArray("files");
            foreach (var f in Files)
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path.Replace('\\', '/'));
                w.WriteNumber("length", f.Length);
                w.WriteString("lastWriteUtc", WorldJson.Utc(f.LastWriteUtc));
                w.WriteString("sha256", f.Sha256);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    internal static BackupSidecar? Parse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(bytes), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var s = new BackupSidecar
            {
                Schema = WorldJson.Int(root, "schema") ?? 1,
                Kind = WorldJson.Str(root, "kind"),
                SourceSaveName = WorldJson.Str(root, "sourceSaveName"),
                CreatedUtc = WorldJson.Time(root, "createdUtc") ?? default,
                PersistenceVersion = WorldJson.Str(root, "persistenceVersion"),
                ProgenitorVersion = WorldJson.Str(root, "progenitorVersion"),
                StartDate = WorldJson.Str(root, "startDate"),
                RestoreOf = WorldJson.Str(root, "restoreOf"),
                RequestId = WorldJson.Str(root, "requestId"),
                Note = WorldJson.Str(root, "note"),
                ChainId = WorldJson.Str(root, "chainId"),
                Trigger = WorldJson.Str(root, "trigger"),
                Attempts = WorldJson.Int(root, "attempts") ?? 0,
                Updates = WorldJson.Str(root, "updates"),
            };
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.Object)
                        s.Files.Add(new WorldManifestEntry
                        {
                            Path = WorldJson.Str(f, "path").Replace('/', System.IO.Path.DirectorySeparatorChar),
                            Length = WorldJson.Long(f, "length") ?? 0,
                            LastWriteUtc = WorldJson.Time(f, "lastWriteUtc") ?? default,
                            Sha256 = WorldJson.Str(f, "sha256"),
                        });
            return s;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// WORLDRESTORE2: <c>BepInEx\progenitor-restore.journal.json</c>, the restore in progress. Written (flushed) before every step
/// that changes the disk, so a crash at any point leaves enough to finish or undo the restore at the next start
/// (<see cref="WorldRestoreRecovery"/>). It stays after a swap until the restored world has saved once
/// (<see cref="Confirmed"/>), keeping the replaced world at <see cref="Outgoing"/> until then.
/// </summary>
internal sealed class RestoreJournal
{
    internal const string PhasePrepare = "prepare";
    internal const string PhaseBackedUp = "backed-up";
    internal const string PhaseStaged = "staged";
    internal const string PhaseSwapping = "swapping";
    internal const string PhaseSwapped = "swapped";

    public int Schema { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public string Phase { get; set; } = PhasePrepare;
    public string SaveFolder { get; set; } = "";
    public string BackupsRoot { get; set; } = "";
    public string Partial { get; set; } = "";
    public string Backup { get; set; } = "";
    public string Incoming { get; set; } = "";
    public string Outgoing { get; set; } = "";
    public bool IncomingVerified { get; set; }
    public string SourceName { get; set; } = "";
    public string BackupName { get; set; } = "";
    public string SettingsPath { get; set; } = "";
    public bool SettingsExisted { get; set; }
    public string SettingsBefore { get; set; } = "";
    public DateTime SettingsMtime { get; set; }
    public bool SettingsWritten { get; set; }
    public string WipeApplied { get; set; } = "";
    public int KeepBackups { get; set; } = WorldBackupCatalog.DefaultKeep;
    public string ProtectSource { get; set; } = "";
    public bool ResultWritten { get; set; }
    public bool Confirmed { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime ConfirmedUtc { get; set; }

    /// <summary>When the swap completed: a save file written later proves the restored world loaded and saved.</summary>
    public DateTime SwappedUtc { get; set; }

    internal byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("requestId", RequestId);
            w.WriteString("phase", Phase);
            w.WriteString("saveFolder", SaveFolder);
            w.WriteString("backupsRoot", BackupsRoot);
            w.WriteString("partial", Partial);
            w.WriteString("backup", Backup);
            w.WriteString("incoming", Incoming);
            w.WriteString("outgoing", Outgoing);
            w.WriteBoolean("incomingVerified", IncomingVerified);
            w.WriteString("sourceName", SourceName);
            w.WriteString("backupName", BackupName);
            w.WriteString("settingsPath", SettingsPath);
            w.WriteBoolean("settingsExisted", SettingsExisted);
            w.WriteString("settingsBefore", SettingsBefore);
            w.WriteString("settingsMtime", WorldJson.Utc(SettingsMtime));
            w.WriteBoolean("settingsWritten", SettingsWritten);
            w.WriteString("wipeApplied", WipeApplied);
            w.WriteNumber("keepBackups", KeepBackups);
            w.WriteString("protectSource", ProtectSource);
            w.WriteBoolean("resultWritten", ResultWritten);
            w.WriteBoolean("confirmed", Confirmed);
            w.WriteString("startedUtc", WorldJson.Utc(StartedUtc));
            w.WriteString("confirmedUtc", WorldJson.Utc(ConfirmedUtc));
            w.WriteString("swappedUtc", WorldJson.Utc(SwappedUtc));
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parse a journal. A hand-written one with plain Windows paths (<c>"C:\x"</c>, unescaped backslashes) is read too: when
    /// the strict parse fails, every backslash is taken literally. Only <c>requestId</c> is required; every other field has a
    /// default.
    /// </summary>
    internal static RestoreJournal? Parse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        var strict = ParseStrict(WorldJson.StripBom(bytes));
        // "C:\new\test" parses strictly into a newline and a tab: a path with a control character is a hand-written one.
        if (strict != null && !HasControl(strict)) return strict;
        string text = WorldJson.Text(bytes);
        if (text.IndexOf('\\') < 0) return null;
        return ParseStrict(new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(text.Replace("\\", "\\\\"))));
    }

    private static bool HasControl(RestoreJournal j)
    {
        foreach (string p in new[] { j.SaveFolder, j.BackupsRoot, j.Partial, j.Backup, j.Incoming, j.Outgoing, j.SettingsPath })
            foreach (char c in p ?? "")
                if (char.IsControl(c)) return true;
        return false;
    }

    private static RestoreJournal? ParseStrict(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var j = new RestoreJournal
            {
                Schema = WorldJson.Int(r, "schema") ?? 1,
                RequestId = WorldJson.Str(r, "requestId"),
                Phase = WorldJson.Str(r, "phase"),
                SaveFolder = WorldJson.Str(r, "saveFolder"),
                BackupsRoot = WorldJson.Str(r, "backupsRoot"),
                Partial = WorldJson.Str(r, "partial"),
                Backup = WorldJson.Str(r, "backup"),
                Incoming = WorldJson.Str(r, "incoming"),
                Outgoing = WorldJson.Str(r, "outgoing"),
                IncomingVerified = WorldJson.Bool(r, "incomingVerified"),
                SourceName = WorldJson.Str(r, "sourceName"),
                BackupName = WorldJson.Str(r, "backupName"),
                SettingsPath = WorldJson.Str(r, "settingsPath"),
                SettingsExisted = WorldJson.Bool(r, "settingsExisted"),
                SettingsBefore = WorldJson.Str(r, "settingsBefore"),
                SettingsMtime = WorldJson.Time(r, "settingsMtime") ?? default,
                SettingsWritten = WorldJson.Bool(r, "settingsWritten"),
                WipeApplied = WorldJson.Str(r, "wipeApplied"),
                KeepBackups = WorldJson.Int(r, "keepBackups") ?? WorldBackupCatalog.DefaultKeep,
                ProtectSource = WorldJson.Str(r, "protectSource"),
                ResultWritten = WorldJson.Bool(r, "resultWritten"),
                Confirmed = WorldJson.Bool(r, "confirmed"),
                StartedUtc = WorldJson.Time(r, "startedUtc") ?? default,
                ConfirmedUtc = WorldJson.Time(r, "confirmedUtc") ?? default,
                SwappedUtc = WorldJson.Time(r, "swappedUtc") ?? default,
            };
            return j.RequestId.Length == 0 ? null : j;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Small JSON helpers of the world restore.</summary>
internal static class WorldJson
{
    internal static string Utc(DateTime t) =>
        t == default ? "" : DateTime.SpecifyKind(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    internal static ReadOnlyMemory<byte> StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? new ReadOnlyMemory<byte>(bytes, 3, bytes.Length - 3)
            : new ReadOnlyMemory<byte>(bytes);

    internal static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    internal static bool Bool(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    internal static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n) ? n : null;

    internal static long? Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n) ? n : null;

    internal static DateTime? Time(JsonElement el, string name)
    {
        string s = Str(el, name);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;
    }

    internal static void Strings(JsonElement el, string name, List<string> into)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String) into.Add(x.GetString() ?? "");
    }

    internal static string Text(byte[] bytes) => Encoding.UTF8.GetString(StripBom(bytes).ToArray());
}
