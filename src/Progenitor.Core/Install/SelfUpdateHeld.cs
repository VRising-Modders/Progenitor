using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// One Progenitor release whose images were read and found to change a protocol (decided 2026-10-09: notify only).
/// Kept so a later session shows it as held without downloading it again (requested 2026-10-09).
/// </summary>
internal sealed class SelfHeldRelease
{
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>The index's download identity: the listing's download URL and file size.</summary>
    public string DownloadUrl { get; set; } = "";

    public long Size { get; set; }

    /// <summary>The sha256 of the package zip that was read.</summary>
    public string PackageSha { get; set; } = "";

    public string CoreSha { get; set; } = "";
    public string BootstrapSha { get; set; } = "";

    /// <summary>The protocols the release carries.</summary>
    public int BootstrapProtocol { get; set; }

    public int CoreProtocol { get; set; }
    public int HandshakeProtocol { get; set; }

    /// <summary>The running protocols it was compared with; another running pair re-evaluates the release.</summary>
    public int RunningBootstrapProtocol { get; set; }

    public int RunningHandshakeProtocol { get; set; }

    /// <summary>What changes (<see cref="SelfProtocols.ChangeOf"/>).</summary>
    public string Change { get; set; } = "";

    public DateTime CheckedUtc { get; set; }

    /// <summary>The index listing is this release (same package, version, download URL and, when both are known, size).</summary>
    public bool IsListing(SelfRelease r) =>
        r != null
        && string.Equals(r.Package, Package, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.Version, Version, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.DownloadUrl, DownloadUrl, StringComparison.Ordinal)
        && (r.Size <= 0 || Size <= 0 || r.Size == Size);

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package,
        ["version"] = Version,
        ["downloadUrl"] = DownloadUrl,
        ["size"] = Size,
        ["packageSha256"] = PackageSha,
        ["coreSha256"] = CoreSha,
        ["bootstrapSha256"] = BootstrapSha,
        ["bootstrapProtocol"] = BootstrapProtocol,
        ["coreProtocol"] = CoreProtocol,
        ["handshakeProtocol"] = HandshakeProtocol,
        ["runningBootstrapProtocol"] = RunningBootstrapProtocol,
        ["runningHandshakeProtocol"] = RunningHandshakeProtocol,
        ["change"] = Change,
        ["checkedUtc"] = CheckedUtc.ToString("o", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The held-release note, <c>BepInEx/config/progenitor-self-update-held.json</c>: Core only (Bootstrap neither compiles nor
/// reads it, so changing it needs no restart). An entry is used only while it still applies (<see cref="WhyStale"/>):
/// the running protocols are the ones it was compared with, its release is still listed with the same download identity,
/// and it is younger than <see cref="MaxAgeDays"/>. Pure model; the file access is <see cref="SelfHeldStore"/>.
/// </summary>
internal sealed class SelfHeldFile
{
    public const string FileName = "progenitor-self-update-held.json";
    public const int Schema = 1;

    /// <summary>An entry older than this is read again (the release is downloaded once more).</summary>
    public const int MaxAgeDays = 30;

    /// <summary>At most this many entries are kept (the oldest go first).</summary>
    public const int MaxEntries = 16;

    public List<SelfHeldRelease> Releases { get; } = new();

    /// <summary>Why <paramref name="e"/> no longer applies, or empty. <paramref name="listed"/> null: the index was not read (not judged).</summary>
    public static string WhyStale(SelfHeldRelease e, IReadOnlyList<SelfRelease>? listed, int runningBootstrap, int runningHandshake, DateTime utcNow)
    {
        if (e.RunningBootstrapProtocol != runningBootstrap || e.RunningHandshakeProtocol != runningHandshake)
            return "the running protocols changed (Bootstrap " + e.RunningBootstrapProtocol + " → " + runningBootstrap
                + ", handshake " + e.RunningHandshakeProtocol + " → " + runningHandshake + ")";
        if (e.CheckedUtc == default || utcNow - e.CheckedUtc > TimeSpan.FromDays(MaxAgeDays) || e.CheckedUtc - utcNow > TimeSpan.FromDays(1))
            return "checked more than " + MaxAgeDays + " days ago";
        if (listed != null && listed.Count > 0)
        {
            bool found = false;
            foreach (var r in listed)
                if (r != null && r.Active && e.IsListing(r)) { found = true; break; }
            if (!found) return "the release left the index";
        }

        return "";
    }

    /// <summary>The held releases that still apply against <paramref name="listed"/> (version → what changes).</summary>
    public Dictionary<string, string> Changes(IReadOnlyList<SelfRelease> listed, int runningBootstrap, int runningHandshake, DateTime utcNow)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (listed == null || listed.Count == 0) return map;
        foreach (var e in Releases)
            if (e.Change.Length > 0 && WhyStale(e, listed, runningBootstrap, runningHandshake, utcNow).Length == 0)
                map[e.Version] = e.Change;
        return map;
    }

    /// <summary>Remove the entries that no longer apply; returns each removed entry's version and reason.</summary>
    public List<(string Version, string Why)> Prune(IReadOnlyList<SelfRelease>? listed, int runningBootstrap, int runningHandshake, DateTime utcNow)
    {
        var removed = new List<(string, string)>();
        for (int i = Releases.Count - 1; i >= 0; i--)
        {
            string why = WhyStale(Releases[i], listed, runningBootstrap, runningHandshake, utcNow);
            if (why.Length == 0) continue;
            removed.Add((Releases[i].Version, why));
            Releases.RemoveAt(i);
        }

        return removed;
    }

    /// <summary>Record a held release (replaces an entry of the same package and version).</summary>
    public void Put(SelfHeldRelease e)
    {
        Releases.RemoveAll(x => string.Equals(x.Package, e.Package, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, e.Version, StringComparison.OrdinalIgnoreCase));
        Releases.Add(e);
        while (Releases.Count > MaxEntries) Releases.RemoveAt(0);
    }

    public static SelfHeldFile Parse(string? json) => Parse(json, out _);

    /// <summary>Parse; <paramref name="ok"/> is false for text that is not this file's JSON object (a torn or foreign file).</summary>
    public static SelfHeldFile Parse(string? json, out bool ok)
    {
        var f = new SelfHeldFile();
        ok = true;
        if (string.IsNullOrWhiteSpace(json)) return f;
        ok = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return f;
            ok = true;
            if (!root.TryGetProperty("held", out var held) || held.ValueKind != JsonValueKind.Array) return f;
            foreach (var row in held.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var e = new SelfHeldRelease
                {
                    Package = Str(row, "package"),
                    Version = Str(row, "version"),
                    DownloadUrl = Str(row, "downloadUrl"),
                    Size = Long(row, "size"),
                    PackageSha = Str(row, "packageSha256"),
                    CoreSha = Str(row, "coreSha256"),
                    BootstrapSha = Str(row, "bootstrapSha256"),
                    BootstrapProtocol = (int)Long(row, "bootstrapProtocol"),
                    CoreProtocol = (int)Long(row, "coreProtocol"),
                    HandshakeProtocol = (int)Long(row, "handshakeProtocol"),
                    RunningBootstrapProtocol = (int)Long(row, "runningBootstrapProtocol"),
                    RunningHandshakeProtocol = (int)Long(row, "runningHandshakeProtocol"),
                    Change = Str(row, "change"),
                    CheckedUtc = Time(row, "checkedUtc"),
                };
                if (e.Version.Length == 0 || e.Version.Length > 64 || e.Change.Length == 0) continue;
                f.Put(e);
            }
        }
        catch
        {
            ok = false;
            return new SelfHeldFile();
        }

        return f;
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("held");
            foreach (var e in Releases)
            {
                w.WriteStartObject();
                w.WriteString("package", e.Package);
                w.WriteString("version", e.Version);
                w.WriteString("downloadUrl", e.DownloadUrl);
                w.WriteNumber("size", e.Size);
                w.WriteString("packageSha256", e.PackageSha);
                w.WriteString("coreSha256", e.CoreSha);
                w.WriteString("bootstrapSha256", e.BootstrapSha);
                w.WriteNumber("bootstrapProtocol", e.BootstrapProtocol);
                w.WriteNumber("coreProtocol", e.CoreProtocol);
                w.WriteNumber("handshakeProtocol", e.HandshakeProtocol);
                w.WriteNumber("runningBootstrapProtocol", e.RunningBootstrapProtocol);
                w.WriteNumber("runningHandshakeProtocol", e.RunningHandshakeProtocol);
                w.WriteString("change", e.Change);
                w.WriteString("checkedUtc", e.CheckedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static DateTime Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : default;
}

/// <summary>
/// Reads and edits <see cref="SelfHeldFile"/> under one lock, atomically (unique temp file, then replace). A file that
/// exists but does not parse is never written over: an edit moves it aside to <c>…held.json.bad-&lt;utc&gt;</c>, logs it and
/// starts from an empty file (the pattern of <see cref="ClientUpdateInputs.EditPins"/>).
/// </summary>
internal static class SelfHeldStore
{
    internal static readonly object Gate = new();

    public static string PathOf(string bepInExRoot) => Path.Combine(bepInExRoot, "config", SelfHeldFile.FileName);

    /// <summary>The file as it is (empty when missing or unreadable; nothing is moved or written).</summary>
    public static SelfHeldFile Read(string bepInExRoot)
    {
        try
        {
            string path = PathOf(bepInExRoot);
            lock (Gate) return File.Exists(path) ? SelfHeldFile.Parse(File.ReadAllText(path)) : new SelfHeldFile();
        }
        catch { return new SelfHeldFile(); }
    }

    /// <summary>Read, change and write under <see cref="Gate"/>. <paramref name="change"/> returns false to write nothing.</summary>
    public static bool Edit(string bepInExRoot, Func<SelfHeldFile, bool> change, Action<string>? log, out string error)
    {
        error = "";
        lock (Gate)
        {
            string path = PathOf(bepInExRoot);
            SelfHeldFile held;
            try
            {
                if (!File.Exists(path)) held = new SelfHeldFile();
                else
                {
                    held = SelfHeldFile.Parse(File.ReadAllText(path), out bool ok);
                    if (!ok)
                    {
                        string aside = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                        File.Move(path, aside, overwrite: true);
                        try { log?.Invoke(SelfHeldFile.FileName + " could not be read; it was kept as " + Path.GetFileName(aside) + " and a new one is started"); }
                        catch { /* */ }
                    }
                }
            }
            catch (Exception e)
            {
                error = "could not read " + SelfHeldFile.FileName + ": " + e.Message;
                return false;
            }

            bool write;
            try { write = change(held); }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            return !write || WriteAtomic(path, held.ToJson(), out error);
        }
    }

    private static bool WriteAtomic(string path, string text, out string error)
    {
        error = "";
        string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* a stray temp file costs nothing */ }
        }
    }
}
