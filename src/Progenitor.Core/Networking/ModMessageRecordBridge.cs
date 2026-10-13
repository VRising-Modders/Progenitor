#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C5: the Core half of the Dev bridge's <c>messages_record</c> op (both sides). Dev reaches this type
/// by reflection (Core grants Dev no <c>InternalsVisibleTo</c>); it is not a plugin API and no network path reaches it.
/// While a session runs, every <c>ctx.Messages</c> message this process sends or receives (for one mod, or every mod
/// when asked with <c>*</c>) is handed to a bounded queue and written as a fixture (<see cref="ModMessageFixtureFormat"/>)
/// under <c>BepInEx/progenitor-dev/messages/&lt;mod&gt;/</c> by a background writer thread, bounded by time, fixture
/// count and bytes (<see cref="ModMessageRecorder"/>), and by a cap on the whole folder across sessions
/// (<see cref="ModMessageRecorder.DiskCapBytes"/>). The session belongs to this Core generation: a Core swap ends it.
/// </summary>
internal static class ModMessageRecordBridge
{
    private static ModMessageRecordQueue? _session;

    /// <summary>The newest session, kept after it stops so <c>status</c> can report it.</summary>
    private static ModMessageRecordQueue? _last;

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>True while a session is recording (cheap; read on every send and receive). Main thread.</summary>
    internal static bool Recording
    {
        get
        {
            var s = _session;
            if (s == null) return false;
            if (s.Recorder.Active) return true;
            End(s);
            return false;
        }
    }

    private static void End(ModMessageRecordQueue s)
    {
        if (!ReferenceEquals(_session, s)) return;
        _session = null;
        s.Complete(2000);
        var r = s.Recorder;
        Plugin.Logger?.LogInfo("Messages: recording stopped (" + (r.StoppedBecause ?? "time") + "): " + r.Written + " fixture(s) written to " + r.Root
                               + (s.Dropped > 0 ? "; " + s.Dropped + " dropped because the write queue was full" : ""));
    }

    /// <summary>The fixture root on this side: <c>BepInEx/progenitor-dev/messages</c>.</summary>
    internal static string RootPath()
    {
        string root = "";
        try { root = BepInEx.Paths.BepInExRootPath ?? ""; }
        catch { /* the default below */ }
        return Path.Combine(root, "progenitor-dev", "messages");
    }

    /// <summary>
    /// Start a session (replacing a running one). <paramref name="mod"/> is a mod GUID, or <c>*</c> for every mod; empty is
    /// refused. Refused too when the fixture folder already holds <see cref="ModMessageRecorder.DiskCapBytes"/>.
    /// </summary>
    public static Dictionary<string, object?> Start(string? mod, int seconds, int maxMessages, int maxBytes)
    {
        string m = (mod ?? "").Trim();
        if (m.Length == 0) return Error("args.mod is required: a mod GUID, or \"*\" for every mod");
        if (m == "*") m = "";
        if (_session != null) End(_session);
        string root = RootPath();
        long existing = ModMessageRecorder.FolderBytes(root);
        int budget = ModMessageRecorder.BudgetFor(maxBytes, existing);
        if (budget == 0)
            return Error("the fixture folder " + root + " already holds " + existing + " bytes (the cap is " + ModMessageRecorder.DiskCapBytes
                         + "); move the fixtures you keep into your test project and delete the folder");
        var recorder = new ModMessageRecorder(root, m, seconds, maxMessages, budget, Now);
        var s = new ModMessageRecordQueue(recorder);
        _session = _last = s;
        Plugin.Logger?.LogInfo("Messages: recording " + (m.Length > 0 ? m : "every mod") + " for up to " + recorder.Seconds + " s, "
                               + recorder.MaxMessages + " fixture(s), " + recorder.MaxBytes + " bytes, to " + recorder.Root);
        var d = Describe(s);
        d["folderBytes"] = existing;
        return d;
    }

    /// <summary>Stop the running session, if any, and report it.</summary>
    public static Dictionary<string, object?> Stop()
    {
        var s = _session;
        if (s != null)
        {
            s.Recorder.Stop();
            End(s);
        }

        return Status();
    }

    /// <summary>The running or last session.</summary>
    public static Dictionary<string, object?> Status()
    {
        _ = Recording;
        if (_last == null)
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["side"] = Side(), ["recording"] = false, ["root"] = RootPath(), ["written"] = 0,
                ["folderBytes"] = ModMessageRecorder.FolderBytes(RootPath()), ["diskCap"] = ModMessageRecorder.DiskCapBytes,
            };
        return Describe(_last);
    }

    private static Dictionary<string, object?> Describe(ModMessageRecordQueue s)
    {
        var d = s.Recorder.Status();
        d["ok"] = true;
        d["side"] = Side();
        d["queued"] = s.Pending;
        d["dropped"] = s.Dropped;
        d["diskCap"] = ModMessageRecorder.DiskCapBytes;
        return d;
    }

    private static string Side() => Plugin.IsServerProcess ? "server" : "client";

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["ok"] = false, ["error"] = message, ["side"] = Side() };

    /// <summary>Main thread: queue one message for the writer (never writes or hashes here).</summary>
    internal static void Offer(string mod, string modVersion, string message, int version, int minReader, byte[] payload, string direction,
        IReadOnlyList<FieldShape>? schema = null)
    {
        var s = _session;
        if (s == null) return;
        if (s.Recorder.Mod.Length > 0 && !string.Equals(s.Recorder.Mod, mod, StringComparison.OrdinalIgnoreCase)) return;
        s.Enqueue(new ModMessageRecord
        {
            Mod = mod,
            ModVersion = modVersion.Length > 0 ? modVersion : "unknown",
            Message = message,
            Version = version,
            MinReaderVersion = minReader,
            Direction = direction,
            Source = "live",
            Payload = payload,
            Schema = schema,
        });
    }

    /// <summary>The Core generation is ending: the session ends with it.</summary>
    internal static void StopForTeardown()
    {
        var s = _session;
        if (s == null) return;
        s.Recorder.Stop();
        End(s);
    }
}
#endif
