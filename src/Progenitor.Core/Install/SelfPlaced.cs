using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// Mod compatibility C4: a Progenitor build placed for one server under its <b>Exact version for all</b>
/// (docs/mod-compatibility.md, Exact applies to Progenitor itself). The server's Core is put in place for that server only,
/// never as the player's preferred version: automatic Progenitor updates wait while it is placed, and once the player has
/// joined with it and is back at the title screen, the copy that ran before is put back (the self-install record too).
/// Pure model; the file is <see cref="SelfPlacedStore"/>.
/// </summary>
internal sealed class SelfPlacement
{
    public const int Schema = 1;

    /// <summary>The swap was requested (hot) or staged (restart) and has not loaded yet.</summary>
    public const string Placing = "placing";

    /// <summary>The placed build runs.</summary>
    public const string Placed = "placed";

    /// <summary>The previous copy is being put back.</summary>
    public const string Restoring = "restoring";

    /// <summary>A placement that was never used for a join is restored after this long.</summary>
    public const double MaxUnusedDays = 3;

    /// <summary>A restore whose swap did not run is tried this many times, then given up (the placed build stays).</summary>
    public const int MaxRestoreAttempts = 3;

    public string State { get; set; } = Placing;

    /// <summary>The server it was placed for, as the join screens named it (display and log only).</summary>
    public string Server { get; set; } = "";

    public string Version { get; set; } = "";
    public string CoreSha { get; set; } = "";

    /// <summary>The copy that ran before, kept in the version store (relative to the BepInEx root).</summary>
    public string PreviousVersion { get; set; } = "";

    public string PreviousCoreSha { get; set; } = "";
    public string PreviousBootstrapSha { get; set; } = "";
    public string PreviousDir { get; set; } = "";

    /// <summary>The self-install record before the placement (written back after the restore), or null when there was none.</summary>
    public SelfInstallRecord? PreviousRecord { get; set; }

    public DateTime PlacedUtc { get; set; }

    /// <summary>A join was accepted while the placed build ran.</summary>
    public bool Joined { get; set; }

    public string Note { get; set; } = "";

    /// <summary>Restores tried that did not bring the player's own copy back.</summary>
    public int RestoreAttempts { get; set; }

    /// <summary>
    /// A build placed for another server that this placement replaced (empty when none): it comes back as the placement when
    /// this one never loads, so the player's own copy is still given back later.
    /// </summary>
    public string EarlierServer { get; set; } = "";

    public string EarlierVersion { get; set; } = "";
    public string EarlierCoreSha { get; set; } = "";
    public bool EarlierJoined { get; set; }
    public DateTime EarlierPlacedUtc { get; set; }

    /// <summary>Remember <paramref name="earlier"/> (a placement in state Placed) as the one this placement replaces.</summary>
    public void RememberEarlier(SelfPlacement earlier)
    {
        if (earlier == null) return;
        EarlierServer = earlier.Server;
        EarlierVersion = earlier.Version;
        EarlierCoreSha = earlier.CoreSha;
        EarlierJoined = earlier.Joined;
        EarlierPlacedUtc = earlier.PlacedUtc;
    }

    /// <summary>The replaced placement as it was (Placed, the same build to give back), or null when there was none.</summary>
    public SelfPlacement? Earlier()
    {
        if (EarlierCoreSha.Length == 0) return null;
        return new SelfPlacement
        {
            State = Placed,
            Server = EarlierServer,
            Version = EarlierVersion,
            CoreSha = EarlierCoreSha,
            PreviousVersion = PreviousVersion,
            PreviousCoreSha = PreviousCoreSha,
            PreviousBootstrapSha = PreviousBootstrapSha,
            PreviousDir = PreviousDir,
            PreviousRecord = PreviousRecord,
            PlacedUtc = EarlierPlacedUtc,
            Joined = EarlierJoined,
        };
    }

    /// <summary>Restore is due: the placed build was used for a join, or it was never used and is old.</summary>
    public bool RestoreDue(DateTime nowUtc) =>
        State == Placed && (Joined || (PlacedUtc != default && (nowUtc - PlacedUtc).TotalDays >= MaxUnusedDays));

    public static SelfPlacement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var p = new SelfPlacement
            {
                State = SelfState.Str(r, "state"),
                Server = SelfState.Str(r, "server"),
                Version = SelfState.Str(r, "version"),
                CoreSha = SelfState.Str(r, "coreSha256"),
                PreviousVersion = SelfState.Str(r, "previousVersion"),
                PreviousCoreSha = SelfState.Str(r, "previousCoreSha256"),
                PreviousBootstrapSha = SelfState.Str(r, "previousBootstrapSha256"),
                PreviousDir = SelfState.Str(r, "previousDir"),
                PlacedUtc = SelfState.Time(r, "placedUtc"),
                Joined = r.TryGetProperty("joined", out var j) && j.ValueKind == JsonValueKind.True,
                Note = SelfState.Str(r, "note"),
                RestoreAttempts = r.TryGetProperty("restoreAttempts", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt32(out int n) ? n : 0,
                EarlierServer = SelfState.Str(r, "earlierServer"),
                EarlierVersion = SelfState.Str(r, "earlierVersion"),
                EarlierCoreSha = SelfState.Str(r, "earlierCoreSha256"),
                EarlierJoined = r.TryGetProperty("earlierJoined", out var ej) && ej.ValueKind == JsonValueKind.True,
                EarlierPlacedUtc = SelfState.Time(r, "earlierPlacedUtc"),
            };
            if (r.TryGetProperty("previousRecord", out var rec) && rec.ValueKind == JsonValueKind.Object)
                p.PreviousRecord = SelfInstallRecord.Parse(rec.GetRawText());
            if (p.State is not (Placing or Placed or Restoring) || p.CoreSha.Length == 0 || p.PreviousCoreSha.Length == 0) return null;
            return p;
        }
        catch (JsonException) { return null; }
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("state", State);
            w.WriteString("server", Server);
            w.WriteString("version", Version);
            w.WriteString("coreSha256", CoreSha);
            w.WriteString("previousVersion", PreviousVersion);
            w.WriteString("previousCoreSha256", PreviousCoreSha);
            w.WriteString("previousBootstrapSha256", PreviousBootstrapSha);
            w.WriteString("previousDir", PreviousDir);
            if (PreviousRecord != null)
            {
                w.WritePropertyName("previousRecord");
                using var rec = JsonDocument.Parse(PreviousRecord.ToJson());
                rec.RootElement.WriteTo(w);
            }

            w.WriteString("placedUtc", PlacedUtc.ToString("o", CultureInfo.InvariantCulture));
            w.WriteBoolean("joined", Joined);
            if (Note.Length > 0) w.WriteString("note", Note);
            if (RestoreAttempts > 0) w.WriteNumber("restoreAttempts", RestoreAttempts);
            if (EarlierCoreSha.Length > 0)
            {
                w.WriteString("earlierServer", EarlierServer);
                w.WriteString("earlierVersion", EarlierVersion);
                w.WriteString("earlierCoreSha256", EarlierCoreSha);
                w.WriteBoolean("earlierJoined", EarlierJoined);
                w.WriteString("earlierPlacedUtc", EarlierPlacedUtc.ToString("o", CultureInfo.InvariantCulture));
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["state"] = State,
        ["server"] = Server,
        ["version"] = Version,
        ["coreSha256"] = CoreSha,
        ["previousVersion"] = PreviousVersion,
        ["previousCoreSha256"] = PreviousCoreSha,
        ["previousDir"] = PreviousDir,
        ["previousRecord"] = PreviousRecord?.Describe(),
        ["placedUtc"] = PlacedUtc.ToString("o", CultureInfo.InvariantCulture),
        ["joined"] = Joined,
        ["note"] = Note,
        ["restoreAttempts"] = RestoreAttempts,
        ["earlier"] = EarlierCoreSha.Length == 0 ? null : new Dictionary<string, object?>
        {
            ["server"] = EarlierServer,
            ["version"] = EarlierVersion,
            ["coreSha256"] = EarlierCoreSha,
            ["joined"] = EarlierJoined,
        },
    };
}

/// <summary>
/// C4: <c>BepInEx/config/progenitor-self-placed.json</c>, Core only (Bootstrap neither compiles nor reads it, so this needs
/// no Bootstrap change). An unreadable file reads as no placement; every call catches.
/// </summary>
internal static class SelfPlacedStore
{
    public const string FileName = "progenitor-self-placed.json";

    public static string PathOf(string bepInExRoot) => Path.Combine(bepInExRoot, "config", FileName);

    public static SelfPlacement? Read(string bepInExRoot)
    {
        try
        {
            string path = PathOf(bepInExRoot);
            return File.Exists(path) ? SelfPlacement.Parse(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Review fix: the copy kept beside the placed build (<c>progenitor-versions/progenitor/&lt;version&gt;-&lt;sha8&gt;/placed.json</c>),
    /// so a damaged placement file can be recovered for the build that runs, and the player's own copy is still given back.
    /// </summary>
    public const string MarkerName = "placed.json";

    public static string MarkerOf(string bepInExRoot, SelfPlacement p) =>
        Path.Combine(SelfPaths.Store(bepInExRoot), SelfPaths.KeptDirName(p.Version, p.CoreSha), MarkerName);

    public static bool Write(string bepInExRoot, SelfPlacement placement, out string error)
    {
        if (!SelfFiles.WriteAtomic(PathOf(bepInExRoot), placement.ToJson(), out error)) return false;
        try
        {
            string marker = MarkerOf(bepInExRoot, placement);
            if (Directory.Exists(Path.GetDirectoryName(marker)!)) SelfFiles.WriteAtomic(marker, placement.ToJson(), out _);
        }
        catch { /* the marker is only for recovery */ }
        return true;
    }

    public static void Delete(string bepInExRoot)
    {
        try
        {
            string path = PathOf(bepInExRoot);
            var p = Read(bepInExRoot);
            if (p != null)
            {
                string marker = MarkerOf(bepInExRoot, p);
                if (File.Exists(marker)) File.Delete(marker);
            }

            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* a stale file is judged again at the next tick */ }
    }

    /// <summary>
    /// Review fix: read the placement; a file that exists but does not parse is moved aside (<c>.bad-&lt;utc&gt;</c>) and
    /// <paramref name="damaged"/> names where, so it is logged once and never read as "no placement" again.
    /// </summary>
    public static SelfPlacement? ReadChecked(string bepInExRoot, out string damaged)
    {
        damaged = "";
        string path = PathOf(bepInExRoot);
        try
        {
            if (!File.Exists(path)) return null;
            var p = SelfPlacement.Parse(File.ReadAllText(path));
            if (p != null) return p;
            string bad = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(path, bad, overwrite: true);
            damaged = bad;
        }
        catch (Exception e) { damaged = path + " (" + e.Message + ")"; }
        return null;
    }

    /// <summary>
    /// Review fix: the placement whose placed build runs now (<paramref name="runningCoreSha"/>), from the marker kept beside
    /// that build, or null when the running build was not placed for a server.
    /// </summary>
    public static SelfPlacement? Recover(string bepInExRoot, string runningCoreSha)
    {
        if (string.IsNullOrEmpty(runningCoreSha)) return null;
        try
        {
            foreach (var k in SelfStore.List(bepInExRoot))
            {
                if (!string.Equals(k.CoreSha, runningCoreSha, StringComparison.OrdinalIgnoreCase)) continue;
                string marker = Path.Combine(SelfPaths.Store(bepInExRoot), k.Dir, MarkerName);
                if (!File.Exists(marker)) continue;
                var p = SelfPlacement.Parse(File.ReadAllText(marker));
                if (p != null && string.Equals(p.CoreSha, runningCoreSha, StringComparison.OrdinalIgnoreCase)) return p;
            }
        }
        catch { /* nothing to recover */ }
        return null;
    }
}
