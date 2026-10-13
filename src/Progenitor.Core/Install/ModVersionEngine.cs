using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>JOINFLOW4: why a store operation refused or failed.</summary>
internal enum ModVersionCode
{
    Ok = 0,
    NotFound = 1,
    HashMismatch = 2,
    AllowlistRefused = 3,
    ManagedElsewhere = 4,
    TwoCopies = 5,
    Busy = 6,
    Queued = 7,
    BadName = 8,
    TooLarge = 9,
    Protected = 10,
    Failed = 11,
    NothingToDo = 12,
}

/// <summary>JOINFLOW4: the answer of a store operation (plain values; the bridge and the screens show <see cref="Message"/>).</summary>
internal sealed class ModVersionResult
{
    public bool Ok { get; init; }
    public ModVersionCode Code { get; init; }
    public string Message { get; init; } = "";

    /// <summary><c>now</c> or <c>restart</c> for a swap, else empty.</summary>
    public string Route { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>
    /// A swap now under another file name: the displaced copy is removed and kept, and the kept copy is written by
    /// <see cref="ModVersionEngine.CompletePlace"/> once the displaced plugin has unloaded.
    /// </summary>
    public bool AwaitingPlace { get; init; }

    public static ModVersionResult Done(string message, string route = "", string version = "") =>
        new() { Ok = true, Code = ModVersionCode.Ok, Message = message, Route = route, Version = version };

    public static ModVersionResult Refuse(ModVersionCode code, string message) =>
        new() { Ok = false, Code = code, Message = message };

    public static string CodeName(ModVersionCode c) => c switch
    {
        ModVersionCode.Ok => "ok",
        ModVersionCode.NotFound => "not-found",
        ModVersionCode.HashMismatch => "hash-mismatch",
        ModVersionCode.AllowlistRefused => "allowlist",
        ModVersionCode.ManagedElsewhere => "managed-elsewhere",
        ModVersionCode.TwoCopies => "two-copies",
        ModVersionCode.Busy => "busy",
        ModVersionCode.Queued => "queued",
        ModVersionCode.BadName => "bad-name",
        ModVersionCode.TooLarge => "too-large",
        ModVersionCode.Protected => "protected",
        ModVersionCode.NothingToDo => "nothing-to-do",
        _ => "failed",
    };
}

/// <summary>JOINFLOW4: the ledger and record rows of one copy (either may be absent).</summary>
internal sealed class ParkedBooks
{
    public static readonly ParkedBooks None = new();
    public ParkedLedgerRow? Ledger { get; init; }
    public ParkedRecordRow? Record { get; init; }
}

/// <summary>
/// JOINFLOW4: the install ledger (<c>progenitor-install.json</c>) and the server-mods record
/// (<c>progenitor-server-mods.txt</c>) describe the <b>active</b> files only. Parking a copy takes its rows out (they
/// travel with the parked entry), and swapping it back in puts them back under the path it lands on. Every call is
/// idempotent, because the journal's recovery may run it twice.
/// </summary>
internal interface IModVersionBooks
{
    /// <summary>The rows of the file at <paramref name="bepRelPath"/> (relative to the BepInEx root), read only.</summary>
    ParkedBooks Peek(string modId, string bepRelPath, string fileName);

    /// <summary>Remove the rows of that file.</summary>
    void Remove(string modId, string bepRelPath, string fileName);

    /// <summary>Put <paramref name="books"/> back for the file now at <paramref name="bepRelPath"/>.</summary>
    void Put(string modId, ParkedBooks books, string bepRelPath, string fileName, string sha256, long size, bool pending);
}

/// <summary>JOINFLOW4: books that keep nothing (tests and a process without a ledger).</summary>
internal sealed class NoModVersionBooks : IModVersionBooks
{
    public static readonly NoModVersionBooks Instance = new();
    public ParkedBooks Peek(string modId, string bepRelPath, string fileName) => ParkedBooks.None;
    public void Remove(string modId, string bepRelPath, string fileName) { }
    public void Put(string modId, ParkedBooks books, string bepRelPath, string fileName, string sha256, long size, bool pending) { }
}

/// <summary>JOINFLOW4: the engine's surroundings (tests supply plain values and fault points).</summary>
internal sealed class ModVersionEnv
{
    public ModVersionEnv(ModVersionPaths paths)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public ModVersionPaths Paths { get; }
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>The reload allowlist's verdict on (file name, sha256): null when it may load, else why not.</summary>
    public Func<string, string, string?> Allow { get; set; } = (_, _) => null;

    public IModVersionBooks Books { get; set; } = NoModVersionBooks.Instance;
    public ModVersionLimits Limits { get; set; } = ModVersionLimits.Default;
    public Action<string>? Info { get; set; }
    public Action<string>? Warn { get; set; }

    /// <summary>Tests: called at each named step of an operation; a throw there is a crash at that point.</summary>
    public Action<string>? Fault { get; set; }
}

/// <summary>How a park leaves the active copy.</summary>
internal enum ParkMode
{
    /// <summary>The active file is removed now: the reloader unloads it within a second.</summary>
    Move = 0,

    /// <summary>The active file stays loaded and gets a <c>.uninstall</c> marker: Bootstrap removes it at the next start, before the promote.</summary>
    CopyAndMark = 1,
}

/// <summary>JOINFLOW4: what to park.</summary>
internal sealed class ParkRequest
{
    public string ModId { get; init; } = "";

    /// <summary>The active file (absolute), in <c>progenitor-reload</c>.</summary>
    public string ActivePath { get; init; } = "";

    public string Version { get; init; } = "";
    public ModVersionSource Source { get; init; }
    public string SourceDetail { get; init; } = "";
    public bool UserInstalled { get; init; }
    public string Level { get; init; } = "";
    public string Rule { get; init; } = "";
    public ParkMode Mode { get; init; }

    /// <summary>What replaces it (the server's version), recorded as the active version, or null.</summary>
    public ActiveVersion? Replacement { get; init; }
}

/// <summary>JOINFLOW4: the active copy a swap displaces (null when there is none).</summary>
internal sealed class SwapActive
{
    public string Path { get; init; } = "";
    public string Version { get; init; } = "";
    public ModVersionSource Source { get; init; }
    public string SourceDetail { get; init; } = "";
    public bool UserInstalled { get; init; }
    public string Level { get; init; } = "";
    public string Rule { get; init; } = "";
}

/// <summary>JOINFLOW4: what to swap in.</summary>
internal sealed class SwapRequest
{
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary>True: the restart route (pending + marker). False: now (the reloader swaps it within a couple of seconds).</summary>
    public bool Restart { get; init; }

    public SwapActive? Active { get; init; }

    /// <summary>The server key it is swapped in for (empty: the player's own choice, which also makes it preferred).</summary>
    public string ForServer { get; init; } = "";

    public string ForServerName { get; init; } = "";
}

/// <summary>
/// JOINFLOW4: the version store's file operations. Every operation that touches more than one file writes a journal
/// (<c>progenitor-versions/journal.json</c>) first, holding the manifest before and after, the files it creates, and
/// the one observable fact that decides whether it committed. A crash anywhere leaves either the old state or the
/// new one: <see cref="Recover"/> (run at Core start) checks that fact and rolls forward or back. Writes go through
/// <c>.part</c> and a move; a parked copy is hash-checked before it is swapped in.
/// </summary>
internal sealed class ModVersionEngine
{
    private readonly ModVersionEnv _env;
    private readonly object _gate = new();
    private string _awaitingMod = "";
    private DateTime _awaitingSince;

    public ModVersionEngine(ModVersionEnv env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    public ModVersionEnv Env => _env;
    private ModVersionPaths P => _env.Paths;

    public ModVersionManifest Read()
    {
        lock (_gate) return ReadLocked();
    }

    private ModVersionManifest ReadLocked()
    {
        var m = ModVersionManifest.Read(P.Manifest, out string error);
        if (error.Length > 0) Warn("JoinFlow: versions: " + ModVersionPaths.ManifestName + " unreadable (" + error + "); read as empty");
        return m;
    }

    public bool JournalPending => File.Exists(P.Journal);

    /// <summary>The journal on disk (raw JSON), or empty.</summary>
    public string JournalText()
    {
        try { return File.Exists(P.Journal) ? File.ReadAllText(P.Journal) : ""; }
        catch { return ""; }
    }

    // ---- park ----------------------------------------------------------------------------------

    public ModVersionResult Park(ParkRequest r)
    {
        lock (_gate)
        {
            try { return ParkLocked(r); }
            catch (Exception e) when (e is not FaultException)
            {
                Warn("JoinFlow: versions: park of " + r.ModId + " threw: " + e.Message);
                TryRecoverLocked();
                return ModVersionResult.Refuse(ModVersionCode.Failed, "Keeping " + r.ModId + " failed: " + e.Message);
            }
        }
    }

    private ModVersionResult ParkLocked(ParkRequest r)
    {
        if (!ModVersionPaths.IsSafeSegment(r.ModId)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The mod id " + r.ModId + " cannot name a folder.");
        string version = (r.Version ?? "").Trim();
        if (!ModVersionPaths.IsSafeSegment(version)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The version " + version + " cannot name a folder.");
        string active = r.ActivePath ?? "";
        string name = Path.GetFileName(active);
        if (!ModVersionPaths.IsSafeDllName(name)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The file " + name + " is not a plain .dll name.");
        if (!File.Exists(active)) return ModVersionResult.Refuse(ModVersionCode.NotFound, name + " is not there.");
        if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
        long size = new FileInfo(active).Length;
        if (size > _env.Limits.MaxFileBytes) return ModVersionResult.Refuse(ModVersionCode.TooLarge, name + " is larger than the store keeps.");
        byte[] bytes = File.ReadAllBytes(active);
        string sha = ModVersionFiles.Sha256Hex(bytes);

        var before = ReadLocked();
        var after = before.Clone();
        var entry = after.GetOrAdd(r.ModId);
        if (entry.Pending != null)
            return ModVersionResult.Refuse(ModVersionCode.Queued, r.ModId + " " + entry.Pending.Version + " is already queued for the next start.");

        DateTime now = _env.Now();
        var books = _env.Books.Peek(r.ModId, P.Relative(active), name);
        var parked = PlaceParked(r.ModId, entry, version, name, sha, size, now, out bool needCopy, out string? orphan);
        parked.Source = r.Source;
        parked.SourceDetail = r.SourceDetail ?? "";
        parked.UserInstalled = r.UserInstalled || parked.UserInstalled;
        parked.Placement = ModVersionPaths.PlacementReload;
        if (r.Level.Length > 0) parked.Level = r.Level;
        if (r.Rule.Length > 0) parked.Rule = r.Rule;
        if (books.Ledger != null) parked.Ledger = books.Ledger;
        if (books.Record != null) parked.Record = books.Record;
        entry.Active = r.Replacement?.Clone();
        if (entry.Preferred.Length == 0 && parked.UserInstalled) entry.Preferred = version;

        string stored = P.StoredFile(r.ModId, parked);
        string marker = UninstallMarkers.MarkerFor(active);
        var journal = new ModVersionJournal
        {
            Op = ModVersionJournal.OpPark,
            ModId = r.ModId,
            Mode = r.Mode == ParkMode.Move ? "move" : "mark",
            Before = before.ToJson(),
            After = after.ToJson(),
            Active = P.Relative(active),
            ActiveSha = sha,
            ActiveFile = name,
            Marker = r.Mode == ParkMode.CopyAndMark ? P.Relative(marker) : "",
            Stored = needCopy ? P.Relative(stored) : "",
            StoredSha = sha,
            Created = needCopy && !File.Exists(stored) ? new List<string> { P.Relative(stored) } : new List<string>(),
        };
        if (orphan != null) journal.DeleteAfter.Add(P.Relative(orphan));
        if (r.Mode == ParkMode.CopyAndMark && !File.Exists(marker)) journal.Created.Add(P.Relative(marker));
        journal.Write(P.Journal);
        Step("park:journal");

        if (needCopy) ModVersionFiles.WriteAtomic(stored, bytes);
        Step("park:stored");

        if (r.Mode == ParkMode.Move)
        {
            File.Delete(active);
            if (File.Exists(active)) throw new IOException(name + " could not be removed");
        }
        else
        {
            ModVersionFiles.WriteAtomic(marker, Array.Empty<byte>());
        }

        Step("park:committed");
        FinishLocked(journal);
        Info("JoinFlow: versions: kept " + r.ModId + " " + version + " (" + name + ", sha256 " + ModVersionFiles.Short(sha) + ") in "
            + ModVersionPaths.FolderName + (r.Mode == ParkMode.Move ? "; removed from progenitor-reload" : "; marked for removal at the next start"));
        EvictLocked(r.ModId, version);
        return ModVersionResult.Done(
            r.Mode == ParkMode.Move
                ? "Your " + version + " is kept for other servers."
                : "Your " + version + " is kept for other servers; it stays loaded until the next start.",
            r.Mode == ParkMode.Move ? "now" : "restart", version);
    }

    /// <summary>
    /// The parked slot for (version, bytes): the same copy refreshed, or a new folder. A slot is only reused without a
    /// copy when its file is on disk with those bytes under that name (review: a row whose file went missing must never
    /// stand in for the copy that is about to be removed). Reused under another file name, the old stored file is
    /// <paramref name="orphan"/>, deleted once the operation commits.
    /// </summary>
    private ParkedVersion PlaceParked(string modId, ModVersionEntry entry, string version, string file, string sha, long size, DateTime now,
        out bool needCopy, out string? orphan)
    {
        orphan = null;
        foreach (var p in entry.Parked)
        {
            if (!string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase)) continue;
            p.LastUsedUtc = now;
            bool sameName = string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase);
            string? onDisk = ModVersionFiles.ShaOf(P.StoredFile(modId, p));
            bool intact = sameName && onDisk != null && string.Equals(onDisk, sha, StringComparison.OrdinalIgnoreCase);
            if (!sameName && onDisk is { Length: > 0 }) orphan = P.StoredFile(modId, p);
            p.File = file;
            p.Size = size;
            needCopy = !intact;
            return p;
        }

        string dir = version;
        bool taken = false;
        foreach (var p in entry.Parked) if (string.Equals(p.Dir, dir, StringComparison.OrdinalIgnoreCase)) taken = true;
        // Two builds of one version live side by side; FindParked answers the most recently parked one.
        if (taken) dir = version + "~" + sha.Substring(0, 8);
        entry.Parked.RemoveAll(p => string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Dir, dir, StringComparison.OrdinalIgnoreCase));
        var slot = new ParkedVersion
        {
            Version = version,
            Dir = dir,
            File = file,
            Sha256 = sha,
            Size = size,
            ParkedUtc = now,
            LastUsedUtc = now,
        };
        entry.Parked.Add(slot);
        needCopy = true;
        return slot;
    }

    // ---- updates (AUTOUPDATE2) -------------------------------------------------------------------

    /// <summary>
    /// AUTOUPDATE2: keep a downloaded, verified copy (an update) in the store so <see cref="SwapIn"/> can place it as any
    /// kept copy. It is the player's own (never evicted unless asked) and carries the ledger row it gets when it is active.
    /// One file, then the manifest: a crash between leaves a stray file the next add of that version overwrites.
    /// </summary>
    public ModVersionResult AddCopy(string modId, string version, string fileName, byte[] bytes, ParkedLedgerRow? ledger,
        ModVersionSource source, string sourceDetail)
    {
        lock (_gate)
        {
            try
            {
                if (!ModVersionPaths.IsSafeSegment(modId)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The mod id " + modId + " cannot name a folder.");
                string v = (version ?? "").Trim();
                if (!ModVersionPaths.IsSafeSegment(v)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The version " + v + " cannot name a folder.");
                if (!ModVersionPaths.IsSafeDllName(fileName)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The file " + fileName + " is not a plain .dll name.");
                if (bytes == null || bytes.Length == 0) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No bytes to keep.");
                if (bytes.LongLength > _env.Limits.MaxFileBytes) return ModVersionResult.Refuse(ModVersionCode.TooLarge, fileName + " is larger than the store keeps.");
                if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
                string sha = ModVersionFiles.Sha256Hex(bytes);
                var m = ReadLocked();
                var entry = m.GetOrAdd(modId);
                var slot = PlaceParked(modId, entry, v, fileName, sha, bytes.LongLength, _env.Now(), out bool needCopy, out string? orphan);
                slot.Source = source;
                slot.SourceDetail = sourceDetail ?? "";
                slot.UserInstalled = true;
                slot.Placement = ModVersionPaths.PlacementReload;
                if (ledger != null) slot.Ledger = ledger;
                string stored = P.StoredFile(modId, slot);
                if (needCopy) ModVersionFiles.WriteAtomic(stored, bytes);
                m.Write(P.Manifest);
                if (orphan != null) ModVersionFiles.TryDelete(orphan);
                Info("JoinFlow: versions: kept the update " + modId + " " + v + " (" + fileName + ", sha256 " + ModVersionFiles.Short(sha) + ") to swap in");
                return ModVersionResult.Done(modId + " " + v + " is kept.", "", v);
            }
            catch (Exception e) when (e is not FaultException)
            {
                Warn("JoinFlow: versions: keeping the update " + modId + " " + version + " threw: " + e.Message);
                return ModVersionResult.Refuse(ModVersionCode.Failed, "Keeping " + modId + " " + version + " failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// AUTOUPDATE2 (phase 1 open issue 1): a swap is queued for the next start and a newer kept copy should load instead.
    /// The queued pending file and its ledger row are superseded. Review M3: journaled like a swap. The journal (manifest
    /// before and after, the image expected) is written first; then the new image replaces the queued file <b>in place</b>
    /// (same name, one atomic move), so there is never a second pending copy of the plugin to promote; then the books and
    /// the manifest. A crash before the move leaves the old queue (undone at Core start); after it, the queue is finished.
    /// The active copy stays marked; the version that was queued stays kept.
    /// </summary>
    public ModVersionResult Requeue(string modId, string version)
    {
        lock (_gate)
        {
            try
            {
                if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
                var before = ReadLocked();
                var after = before.Clone();
                var entry = after.Find(modId);
                if (entry?.Pending == null) return ModVersionResult.Refuse(ModVersionCode.NothingToDo, "Nothing of " + modId + " is queued for the next start.");
                var p = entry.FindUsable(version);
                if (p == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No kept copy of " + modId + " " + version + ".");
                var old = entry.Pending;
                string target = P.Absolute(old.PendingFile);
                string? oldNow = ModVersionFiles.ShaOf(target);
                if (oldNow == null || (oldNow.Length > 0 && !string.Equals(oldNow, old.Sha256, StringComparison.OrdinalIgnoreCase)))
                    return ModVersionResult.Refuse(ModVersionCode.TwoCopies, "The queued " + Path.GetFileName(target) + " changed on disk, so it is not replaced.");
                string stored = P.StoredFile(modId, p);
                byte[] bytes = File.ReadAllBytes(stored);
                string sha = ModVersionFiles.Sha256Hex(bytes);
                if (!string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ModVersionResult.Refuse(ModVersionCode.HashMismatch, ModVersionText.HashMismatch(modId, version, p.Sha256, sha));
                string? allow = _env.Allow(p.File, sha);
                if (allow != null) return ModVersionResult.Refuse(ModVersionCode.AllowlistRefused, ModVersionText.Allowlist(modId, version, p.File, allow));

                DateTime now = _env.Now();
                p.LastUsedUtc = now;
                entry.Preferred = p.Version;
                entry.Pending = new PendingSwap
                {
                    Version = p.Version,
                    PendingFile = old.PendingFile,
                    // The name the queue promotes to stays the queued one: one file per plugin in progenitor-pending.
                    File = old.File,
                    Sha256 = sha,
                    Marked = old.Marked,
                    ForServer = "",
                    ForServerName = "",
                    QueuedUtc = now,
                };
                var journal = new ModVersionJournal
                {
                    Op = ModVersionJournal.OpRequeue,
                    ModId = modId,
                    Mode = "restart",
                    Before = before.ToJson(),
                    After = after.ToJson(),
                    Target = old.PendingFile,
                    TargetSha = sha,
                    ActiveSha = old.Sha256,
                    Stored = P.Relative(stored),
                    StoredSha = sha,
                    IncomingLedger = p.Ledger,
                    IncomingRecord = p.Record,
                    IncomingSize = bytes.LongLength,
                };
                journal.Write(P.Journal);
                Step("requeue:journal");
                ModVersionFiles.WriteAtomic(target, bytes);
                Step("requeue:placed");
                FinishRequeueLocked(journal);
                Info("JoinFlow: versions: queued " + modId + " " + version + " (" + Path.GetFileName(target) + ", sha256 " + ModVersionFiles.Short(sha)
                    + ") in place of the queued " + old.Version + "; it loads at the next start");
                return ModVersionResult.Done(ModVersionText.QueuedNote(version), "restart", version);
            }
            catch (Exception e) when (e is not FaultException)
            {
                Warn("JoinFlow: versions: requeue of " + modId + " " + version + " threw: " + e.Message);
                TryRecoverLocked();
                return ModVersionResult.Refuse(ModVersionCode.Failed, "Queuing " + modId + " " + version + " failed: " + e.Message);
            }
        }
    }

    /// <summary>The committed half of a requeue: the pending row names the new image, the manifest the new queue.</summary>
    private void FinishRequeueLocked(ModVersionJournal j)
    {
        try
        {
            string name = Path.GetFileName(j.Target);
            _env.Books.Remove(j.ModId, j.Target, name);
            if (j.IncomingLedger != null || j.IncomingRecord != null)
                _env.Books.Put(j.ModId, new ParkedBooks { Ledger = j.IncomingLedger, Record = j.IncomingRecord }, j.Target, name, j.TargetSha, j.IncomingSize, pending: true);
        }
        catch (Exception e) { Warn("JoinFlow: versions: the install records could not be updated for " + j.ModId + ": " + e.Message); }

        Step("requeue:books");
        WriteManifestJson(j.After);
        ModVersionFiles.TryDelete(P.Journal);
        _orphanStamp = "";
    }

    private string _orphanStamp = "";

    /// <summary>How often the orphan check parsed the manifest (tests: it parses only when the file changed).</summary>
    public int OrphanManifestReads { get; private set; }
    private readonly List<(string Id, string PendingRel)> _queued = new();

    /// <summary>
    /// AUTOUPDATE2: a swap queued for the next start whose pending file is gone in this session (a Cancel install deleted
    /// it) would still remove the marked active copy at the next start and leave the player without the mod. Each such
    /// queue is dropped: the active copy's removal marker goes and its install-ledger row comes back. A file the start
    /// already promoted is not an orphan (the reload copy holds the queued bytes). One line per change; the ids dropped in
    /// <paramref name="dropped"/>. Review L3: cheap when nothing is queued: the manifest is parsed only when its file
    /// changed, and the queued files are checked with one existence test each.
    /// </summary>
    public List<string> UnqueueOrphans(out List<string> dropped)
    {
        var lines = new List<string>();
        dropped = new List<string>();
        lock (_gate)
        {
            try
            {
                if (JournalPending || _awaitingMod.Length > 0) return lines;
                var info = new FileInfo(P.Manifest);
                if (!info.Exists)
                {
                    _queued.Clear();
                    _orphanStamp = "absent";
                    return lines;
                }

                string stamp = info.LastWriteTimeUtc.Ticks + "/" + info.Length;
                if (!string.Equals(stamp, _orphanStamp, StringComparison.Ordinal))
                {
                    _queued.Clear();
                    OrphanManifestReads++;
                    foreach (var e in ReadLocked().Mods)
                        if (e.Pending != null) _queued.Add((e.Id, e.Pending.PendingFile));
                    _orphanStamp = stamp;
                }

                bool missing = false;
                foreach (var q in _queued)
                    if (!File.Exists(P.Absolute(q.PendingRel))) { missing = true; break; }
                if (!missing) return lines;

                var m = ReadLocked();
                bool changed = false;
                foreach (var e in m.Mods)
                {
                    var q = e.Pending;
                    if (q == null || File.Exists(P.Absolute(q.PendingFile))) continue;
                    string? promoted = ModVersionFiles.ShaOf(Path.Combine(P.Reload, q.File));
                    if (promoted != null && string.Equals(promoted, q.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    if (q.Marked.Length > 0)
                    {
                        string active = P.Absolute(q.Marked);
                        ModVersionFiles.TryDelete(UninstallMarkers.MarkerFor(active));
                        string? activeSha = ModVersionFiles.ShaOf(active);
                        ParkedVersion? slot = null;
                        foreach (var p in e.Parked)
                            if (activeSha is { Length: > 0 } && string.Equals(p.Sha256, activeSha, StringComparison.OrdinalIgnoreCase)) slot = p;
                        if (slot?.Ledger != null || slot?.Record != null)
                        {
                            try
                            {
                                _env.Books.Put(e.Id, new ParkedBooks { Ledger = slot!.Ledger, Record = slot.Record }, q.Marked, Path.GetFileName(active),
                                    activeSha!, new FileInfo(active).Length, pending: false);
                            }
                            catch (Exception ex) { Warn("JoinFlow: versions: the install records could not be restored for " + e.Id + ": " + ex.Message); }
                        }
                    }

                    lines.Add(e.Id + " " + q.Version + ": its queued copy was removed, so the copy in use stays (no longer removed at the next start)");
                    dropped.Add(e.Id);
                    e.Pending = null;
                    changed = true;
                }

                if (changed)
                {
                    m.Compact();
                    m.Write(P.Manifest);
                }

                _orphanStamp = "";
            }
            catch (Exception e) when (e is not FaultException)
            {
                Warn("JoinFlow: versions: checking queued swaps threw: " + e.Message);
            }
        }

        foreach (var l in lines) Info("JoinFlow: versions: " + l);
        return lines;
    }

    public List<string> UnqueueOrphans() => UnqueueOrphans(out _);

    // ---- swap ----------------------------------------------------------------------------------

    public ModVersionResult SwapIn(SwapRequest r)
    {
        lock (_gate)
        {
            try { return SwapLocked(r); }
            catch (Exception e) when (e is not FaultException)
            {
                Warn("JoinFlow: versions: swap of " + r.ModId + " " + r.Version + " threw: " + e.Message);
                // In this process the displaced plugin may still be loaded: undo, never roll forward beside it.
                TryRecoverLocked(forceUndo: true);
                _awaitingMod = "";
                return ModVersionResult.Refuse(ModVersionCode.Failed, "Swapping in " + r.ModId + " " + r.Version + " failed: " + e.Message);
            }
        }
    }

    private ModVersionResult SwapLocked(SwapRequest r)
    {
        if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
        var before = ReadLocked();
        var after = before.Clone();
        var entry = after.Find(r.ModId);
        // 2026-10-05: a copy a server sent is never swapped in (FindUsable skips it).
        var p = entry?.FindUsable(r.Version);
        if (entry == null || p == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No kept copy of " + r.ModId + " " + r.Version + ".");
        if (entry.Pending != null)
            return ModVersionResult.Refuse(ModVersionCode.Queued, r.ModId + " " + entry.Pending.Version + " is already queued for the next start.");

        string stored = P.StoredFile(r.ModId, p);
        if (!File.Exists(stored))
            return ModVersionResult.Refuse(ModVersionCode.NotFound, "The kept copy of " + r.ModId + " " + r.Version + " is gone from " + ModVersionPaths.FolderName + ".");
        byte[] bytes = File.ReadAllBytes(stored);
        string sha = ModVersionFiles.Sha256Hex(bytes);
        if (!string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase))
            return ModVersionResult.Refuse(ModVersionCode.HashMismatch, ModVersionText.HashMismatch(r.ModId, r.Version, p.Sha256, sha));

        string? allow = _env.Allow(p.File, sha);
        if (allow != null) return ModVersionResult.Refuse(ModVersionCode.AllowlistRefused, ModVersionText.Allowlist(r.ModId, r.Version, p.File, allow));

        string target = Path.Combine(r.Restart ? P.Pending : P.Reload, p.File);
        string activePath = r.Active?.Path ?? "";
        bool hasActive = activePath.Length > 0 && File.Exists(activePath);
        if (r.Active != null && r.Active.Path.Length > 0 && !hasActive)
            return ModVersionResult.Refuse(ModVersionCode.NotFound, Path.GetFileName(activePath) + " is not there any more.");
        bool sameFile = hasActive && string.Equals(Path.GetFullPath(activePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        string? existing = ModVersionFiles.ShaOf(target);
        if (existing == null) return ModVersionResult.Refuse(ModVersionCode.Failed, p.File + " could not be read.");
        if (!sameFile && existing.Length > 0 && !string.Equals(existing, sha, StringComparison.OrdinalIgnoreCase))
            return ModVersionResult.Refuse(ModVersionCode.TwoCopies, ModVersionText.InTheWay(p.File, r.Restart));

        DateTime now = _env.Now();
        byte[]? activeBytes = null;
        string activeSha = "", activeName = "", activeStored = "";
        bool activeCopy = false;
        string? activeOrphan = null;
        ParkedBooks activeBooks = ParkedBooks.None;
        if (hasActive)
        {
            activeName = Path.GetFileName(activePath);
            if (!ModVersionPaths.IsSafeDllName(activeName)) return ModVersionResult.Refuse(ModVersionCode.BadName, activeName + " is not a plain .dll name.");
            string av = (r.Active!.Version ?? "").Trim();
            if (!ModVersionPaths.IsSafeSegment(av)) return ModVersionResult.Refuse(ModVersionCode.BadName, "The active version " + av + " cannot name a folder.");
            activeBytes = File.ReadAllBytes(activePath);
            activeSha = ModVersionFiles.Sha256Hex(activeBytes);
            if (string.Equals(activeSha, sha, StringComparison.OrdinalIgnoreCase))
                return ModVersionResult.Refuse(ModVersionCode.NothingToDo, r.ModId + " " + r.Version + " is already the active copy.");
            activeBooks = _env.Books.Peek(r.ModId, P.Relative(activePath), activeName);
            var slot = PlaceParked(r.ModId, entry, av, activeName, activeSha, activeBytes.LongLength, now, out activeCopy, out activeOrphan);
            slot.Source = r.Active.Source;
            slot.SourceDetail = r.Active.SourceDetail ?? "";
            slot.UserInstalled = slot.UserInstalled || r.Active.UserInstalled;
            slot.Placement = ModVersionPaths.PlacementReload;
            if (r.Active.Level.Length > 0) slot.Level = r.Active.Level;
            if (r.Active.Rule.Length > 0) slot.Rule = r.Active.Rule;
            if (activeBooks.Ledger != null) slot.Ledger = activeBooks.Ledger;
            if (activeBooks.Record != null) slot.Record = activeBooks.Record;
            if (entry.Preferred.Length == 0 && slot.UserInstalled) entry.Preferred = av;
            activeStored = P.StoredFile(r.ModId, slot);
        }

        // The incoming copy (the row found above: a displaced build of the same version is a different row): gone from
        // the store once it is active (now), kept until the start promotes it (restart).
        p.LastUsedUtc = now;
        if (r.ForServer.Length == 0) entry.Preferred = p.Version;
        // The restart route leaves the active copy loaded and marks it: Bootstrap deletes a marked reload file at the
        // next start before it promotes the pending one, so the two are never loaded together.
        string marker = r.Restart && hasActive ? UninstallMarkers.MarkerFor(activePath) : "";
        if (r.Restart)
        {
            entry.Pending = new PendingSwap
            {
                Version = p.Version,
                PendingFile = P.Relative(target),
                File = p.File,
                Sha256 = sha,
                Marked = marker.Length > 0 ? P.Relative(activePath) : "",
                ForServer = r.ForServer ?? "",
                ForServerName = r.ForServerName ?? "",
                QueuedUtc = now,
            };
        }
        else
        {
            entry.Parked.Remove(p);
            entry.Active = new ActiveVersion
            {
                Version = p.Version,
                File = p.File,
                Sha256 = sha,
                ForServer = r.ForServer ?? "",
                ForServerName = r.ForServerName ?? "",
                Source = p.Source,
            };
        }

        var journal = new ModVersionJournal
        {
            Op = ModVersionJournal.OpSwap,
            ModId = r.ModId,
            Mode = r.Restart ? "restart" : "now",
            Before = before.ToJson(),
            After = after.ToJson(),
            Active = hasActive ? P.Relative(activePath) : "",
            ActiveSha = activeSha,
            ActiveFile = activeName,
            Target = P.Relative(target),
            TargetSha = sha,
            Stored = P.Relative(stored),
            StoredSha = sha,
            Marker = marker.Length > 0 ? P.Relative(marker) : "",
            // Always the displaced copy's kept file (written now, or already kept and intact): undo reads it back.
            ActiveStored = hasActive ? P.Relative(activeStored) : "",
            Created = new List<string>(),
            IncomingLedger = p.Ledger,
            IncomingRecord = p.Record,
            IncomingSize = bytes.LongLength,
        };
        if (activeCopy && !File.Exists(activeStored)) journal.Created.Add(P.Relative(activeStored));
        if (activeOrphan != null) journal.DeleteAfter.Add(P.Relative(activeOrphan));
        if (marker.Length > 0 && !File.Exists(marker)) journal.Created.Add(P.Relative(marker));
        if (existing.Length == 0) journal.Created.Add(P.Relative(target));
        journal.Write(P.Journal);
        Step("swap:journal");

        if (activeCopy && activeBytes != null) ModVersionFiles.WriteAtomic(activeStored, activeBytes);
        Step("swap:stored");

        if (!r.Restart)
        {
            // Never two copies of one plugin GUID loaded together. The same name is replaced in place: the reloader's
            // own unload-then-load in one scan. Under another name the displaced file goes first and the kept copy is
            // written only once the caller has seen the displaced plugin unload (CompletePlace): the reloader loads a
            // settled new file inside its file loop and unloads removed ones only after it, so writing both in one go
            // could load the new copy in the scan that still has the old one (review finding 1). The journal stays
            // until then; a crash in between rolls forward from the kept copy.
            if (hasActive && !sameFile)
            {
                File.Delete(activePath);
                if (File.Exists(activePath)) throw new IOException(activeName + " could not be removed");
                Step("swap:removed");
                _awaitingMod = r.ModId;
                _awaitingSince = now;
                Info("JoinFlow: versions: removed " + activeName + " (" + r.ModId + " " + r.Active!.Version + ", kept); " + p.File
                    + " (" + r.Version + ") is written once it has unloaded");
                return new ModVersionResult
                {
                    Ok = true, Code = ModVersionCode.Ok, Route = "now", Version = r.Version, AwaitingPlace = true,
                    Message = ModVersionText.AwaitingNote(r.Version),
                };
            }

            Step("swap:removed");
            ModVersionFiles.WriteAtomic(target, bytes);
        }
        else
        {
            if (marker.Length > 0) ModVersionFiles.WriteAtomic(marker, Array.Empty<byte>());
            Step("swap:marked");
            ModVersionFiles.WriteAtomic(target, bytes);
        }

        Step("swap:placed");
        FinishLocked(journal);
        Info("JoinFlow: versions: " + (r.Restart ? "queued " : "swapped in ") + r.ModId + " " + r.Version + " (" + p.File + ", sha256 "
            + ModVersionFiles.Short(sha) + ")" + (hasActive ? "; kept " + r.Active!.Version : "")
            + (r.ForServer.Length > 0 ? " for " + r.ForServer : "") + (r.Restart ? "; it loads at the next start" : ""));
        EvictLocked(r.ModId, hasActive ? (r.Active!.Version ?? "") : "");
        return ModVersionResult.Done(
            r.Restart ? ModVersionText.QueuedNote(r.Version) : ModVersionText.SwappedNote(r.Version),
            r.Restart ? "restart" : "now", r.Version);
    }

    /// <summary>The mod whose swap waits for its displaced plugin to unload (this engine's swap), or empty.</summary>
    public string AwaitingMod
    {
        get { lock (_gate) return _awaitingMod; }
    }

    /// <summary>When that swap removed the displaced copy.</summary>
    public DateTime AwaitingSince
    {
        get { lock (_gate) return _awaitingSince; }
    }

    /// <summary>
    /// The second half of a swap under another file name: write the kept copy (hash-checked again) now that the
    /// displaced plugin has unloaded, and commit. Idempotent: with nothing waiting it answers <c>nothing-to-do</c>.
    /// </summary>
    public ModVersionResult CompletePlace()
    {
        lock (_gate)
        {
            try
            {
                var j = File.Exists(P.Journal) ? ModVersionJournal.Read(P.Journal, out _) : null;
                if (j == null || j.Op != ModVersionJournal.OpSwap || j.Mode != "now")
                {
                    _awaitingMod = "";
                    return ModVersionResult.Refuse(ModVersionCode.NothingToDo, "No swap is waiting.");
                }

                string target = P.Absolute(j.Target);
                string? at = ModVersionFiles.ShaOf(target);
                if (at == null || (at.Length > 0 && !string.Equals(at, j.TargetSha, StringComparison.OrdinalIgnoreCase)))
                {
                    // Something else took the name meanwhile: never overwrite it, never put the displaced copy back
                    // beside it; the displaced copy stays kept.
                    string rec = AbortPlaceLocked(j, "another file took " + Path.GetFileName(target));
                    return ModVersionResult.Refuse(ModVersionCode.TwoCopies, ModVersionText.InTheWay(Path.GetFileName(target), false) + " (" + rec + ")");
                }

                if (at.Length == 0)
                {
                    byte[] bytes = File.ReadAllBytes(P.Absolute(j.Stored));
                    string sha = ModVersionFiles.Sha256Hex(bytes);
                    if (!string.Equals(sha, j.TargetSha, StringComparison.OrdinalIgnoreCase))
                    {
                        string rec = TryRecoverLocked(forceUndo: true);
                        _awaitingMod = "";
                        return ModVersionResult.Refuse(ModVersionCode.HashMismatch,
                            ModVersionText.HashMismatch(j.ModId, VersionIn(j.After, j.ModId), j.TargetSha, sha) + " (" + rec + ")");
                    }

                    ModVersionFiles.WriteAtomic(target, bytes);
                }

                Step("swap:placed");
                FinishLocked(j);
                _awaitingMod = "";
                string version = VersionIn(j.After, j.ModId);
                Info("JoinFlow: versions: swapped in " + j.ModId + " " + version + " (" + Path.GetFileName(target) + ", sha256 "
                    + ModVersionFiles.Short(j.TargetSha) + ") after " + j.ActiveFile + " unloaded");
                EvictLocked(j.ModId, "");
                return ModVersionResult.Done(ModVersionText.SwappedNote(version), "now", version);
            }
            catch (Exception e) when (e is not FaultException)
            {
                // Undo rather than roll forward with the write that just failed: the displaced copy comes back.
                Warn("JoinFlow: versions: placing the kept copy threw: " + e.Message + "; undoing the swap");
                TryRecoverLocked(forceUndo: true);
                _awaitingMod = "";
                return ModVersionResult.Refuse(ModVersionCode.Failed, "Placing the kept copy failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Give up a swap that waits for its unload (the displaced plugin never unloaded): the displaced copy goes back to
    /// its place (the reloader sees a changed file and keeps one copy), the kept one stays kept.
    /// </summary>
    public string AbortWaiting(string why)
    {
        lock (_gate)
        {
            if (_awaitingMod.Length == 0) return "nothing waits";
            Warn("JoinFlow: versions: the swap of " + _awaitingMod + " is undone: " + why);
            string r = TryRecoverLocked(forceUndo: true);
            _awaitingMod = "";
            return r;
        }
    }

    /// <summary>
    /// Give up a waiting swap because another copy of the mod took over (another install wrote one): the displaced copy
    /// stays kept and is not put back beside it; the incoming copy stays kept; the active copy is no longer the store's.
    /// </summary>
    public string AbortPlace(string why)
    {
        lock (_gate)
        {
            var j = File.Exists(P.Journal) ? ModVersionJournal.Read(P.Journal, out _) : null;
            if (j == null || j.Op != ModVersionJournal.OpSwap || j.Mode != "now")
            {
                _awaitingMod = "";
                return "nothing waits";
            }

            return AbortPlaceLocked(j, why);
        }
    }

    private string AbortPlaceLocked(ModVersionJournal j, string why)
    {
        try
        {
            var before = ModVersionManifest.Parse(j.Before, out _);
            var after = ModVersionManifest.Parse(j.After, out _);
            var kept = after.Find(j.ModId)?.Parked.Find(p => string.Equals(P.Relative(P.StoredFile(j.ModId, p)), j.ActiveStored, StringComparison.OrdinalIgnoreCase));
            var e = before.GetOrAdd(j.ModId);
            if (kept != null && StoredIntact(j.ActiveStored, j.ActiveSha))
            {
                e.Parked.RemoveAll(p => string.Equals(p.Dir, kept.Dir, StringComparison.OrdinalIgnoreCase));
                e.Parked.Add(kept.Clone());
            }

            e.Active = null;
            foreach (var rel in j.Created)
            {
                if (string.Equals(rel, j.ActiveStored, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(rel, j.Target, StringComparison.OrdinalIgnoreCase)) continue;
                ModVersionFiles.TryDelete(P.Absolute(rel));
            }

            try { if (j.Active.Length > 0) _env.Books.Remove(j.ModId, j.Active, j.ActiveFile); }
            catch (Exception ex) { Warn("JoinFlow: versions: the install records could not be updated for " + j.ModId + ": " + ex.Message); }
            before.Compact();
            before.Write(P.Manifest);
            ModVersionFiles.TryDelete(P.Journal);
            _awaitingMod = "";
            Warn("JoinFlow: versions: the swap of " + j.ModId + " stopped (" + why + "); " + j.ActiveFile + " stays kept");
            return "stopped " + j.ModId;
        }
        catch (Exception ex)
        {
            Warn("JoinFlow: versions: stopping the swap of " + j.ModId + " threw: " + ex.Message + "; the journal is left for the next start");
            _awaitingMod = "";
            return "error: " + ex.Message;
        }
    }

    /// <summary>The active version a journal's manifest records for a mod (for the log and the answer).</summary>
    private static string VersionIn(string manifestJson, string modId) =>
        ModVersionManifest.Parse(manifestJson, out _).Find(modId)?.Active?.Version ?? "";

    // ---- journal ------------------------------------------------------------------------------

    /// <summary>
    /// Finish or undo an operation a crash interrupted. The committing fact: a park moved (the active file is no
    /// longer those bytes) or marked (the marker exists); a swap placed its file (the target holds the incoming bytes),
    /// or removed the active copy, in which case the target is written from the kept copy and the swap rolls forward.
    /// Anything short of that rolls back: the files this operation created are deleted and the old manifest is written.
    /// </summary>
    public string Recover()
    {
        lock (_gate) return TryRecoverLocked();
    }

    private string TryRecoverLocked(bool forceUndo = false)
    {
        try { return RecoverLocked(forceUndo); }
        catch (Exception e)
        {
            Warn("JoinFlow: versions: recovering the journal threw: " + e.Message + "; it is left for the next start");
            return "error: " + e.Message;
        }
    }

    /// <summary>A kept file to read back: there, and those bytes (an empty path is never read).</summary>
    private bool Readable(string rel, string sha) => !string.IsNullOrEmpty(rel) && StoredIntact(rel, sha);

    private string RecoverLocked(bool forceUndo = false)
    {
        if (!File.Exists(P.Journal)) return "none";
        var j = ModVersionJournal.Read(P.Journal, out string error);
        if (j == null)
        {
            // A journal that cannot be read describes nothing this build can act on; keep it for a human.
            string bad = P.Journal + ".unreadable";
            try { File.Move(P.Journal, bad, overwrite: true); }
            catch { /* left in place */ }
            Warn("JoinFlow: versions: the journal is unreadable (" + error + "); moved to " + Path.GetFileName(bad) + "; the manifest is unchanged");
            return "unreadable";
        }

        if (j.Op == ModVersionJournal.OpRequeue)
        {
            // Review M3: the move is the commit. The new image in place: finish (books, manifest). Anything else (the old
            // image, or no file): the old queue stands, its manifest is written back.
            string? now = ModVersionFiles.ShaOf(P.Absolute(j.Target));
            _awaitingMod = "";
            if (!forceUndo && now != null && string.Equals(now, j.TargetSha, StringComparison.OrdinalIgnoreCase))
            {
                FinishRequeueLocked(j);
                Info("JoinFlow: versions: recovered an interrupted requeue of " + j.ModId + ": finished");
                return "finished requeue " + j.ModId;
            }

            WriteManifestJson(j.Before);
            ModVersionFiles.TryDelete(P.Journal);
            _orphanStamp = "";
            Info("JoinFlow: versions: recovered an interrupted requeue of " + j.ModId + ": undone (the queued copy stays)");
            return "undone requeue " + j.ModId;
        }

        bool committed = false;
        string? targetNow = j.Op == ModVersionJournal.OpSwap ? ModVersionFiles.ShaOf(P.Absolute(j.Target)) : "";
        if (forceUndo)
        {
            // The caller gave up (a failed write, an unload that never came): undo, never roll forward.
            committed = false;
        }
        else if (j.Op == ModVersionJournal.OpPark)
        {
            if (j.Mode == "move")
            {
                string? now = ModVersionFiles.ShaOf(P.Absolute(j.Active));
                committed = now != null && !string.Equals(now, j.ActiveSha, StringComparison.OrdinalIgnoreCase)
                    && StoredIntact(j.Stored, j.StoredSha);
            }
            else committed = j.Marker.Length > 0 && File.Exists(P.Absolute(j.Marker));
        }
        else
        {
            committed = targetNow != null && string.Equals(targetNow, j.TargetSha, StringComparison.OrdinalIgnoreCase);
            if (!committed && j.Mode == "now" && j.Active.Length > 0
                && !string.Equals(Path.GetFullPath(P.Absolute(j.Active)), Path.GetFullPath(P.Absolute(j.Target)), StringComparison.OrdinalIgnoreCase))
            {
                // The displaced copy is already gone (this is a later process, or a later Core, so it is unloaded):
                // roll forward from the kept copy (hash-checked), unless something else took the target's name
                // meanwhile, which is never overwritten.
                string? activeNow = ModVersionFiles.ShaOf(P.Absolute(j.Active));
                if (activeNow == "" && targetNow == "" && Readable(j.Stored, j.StoredSha) && StoredIntact(j.ActiveStored, j.ActiveSha))
                {
                    try
                    {
                        ModVersionFiles.WriteAtomic(P.Absolute(j.Target), File.ReadAllBytes(P.Absolute(j.Stored)));
                        committed = true;
                    }
                    catch (Exception e) { Warn("JoinFlow: versions: rolling the swap of " + j.ModId + " forward failed (" + e.Message + "); undoing it"); }
                }
            }
        }

        _awaitingMod = "";
        if (committed)
        {
            FinishLocked(j);
            Info("JoinFlow: versions: recovered an interrupted " + j.Op + " of " + j.ModId + ": finished");
            return "finished " + j.Op + " " + j.ModId;
        }

        // Undo. The displaced copy comes back first when it is gone or overwritten (a same-name write that failed its
        // read-back deletes the target): its kept copy is the only one left, so it is never deleted before that
        // (review finding 2). When it cannot come back, the kept copy and its row stay.
        bool keepActiveCopy = false;
        if (j.Active.Length > 0 && j.ActiveStored.Length > 0)
        {
            string activeAbs = P.Absolute(j.Active);
            string? activeNow = ModVersionFiles.ShaOf(activeAbs);
            // Unreadable: its state is unknown, so its kept copy stays (review finding 10).
            if (activeNow == null) keepActiveCopy = true;
            else if (!string.Equals(activeNow, j.ActiveSha, StringComparison.OrdinalIgnoreCase))
            {
                if (Readable(j.ActiveStored, j.ActiveSha))
                {
                    try
                    {
                        ModVersionFiles.WriteAtomic(activeAbs, File.ReadAllBytes(P.Absolute(j.ActiveStored)));
                        Warn("JoinFlow: versions: put " + j.ActiveFile + " back from its kept copy");
                    }
                    catch (Exception e)
                    {
                        keepActiveCopy = true;
                        Warn("JoinFlow: versions: " + j.ActiveFile + " could not be put back (" + e.Message + "); its kept copy stays in "
                            + ModVersionPaths.FolderName);
                    }
                }
                else keepActiveCopy = true;
            }
        }

        foreach (var rel in j.Created)
        {
            if (keepActiveCopy && string.Equals(rel, j.ActiveStored, StringComparison.OrdinalIgnoreCase)) continue;
            // The target is deleted only when it holds this operation's bytes: a file something else wrote under that
            // name meanwhile is not the store's to delete.
            if (j.Op == ModVersionJournal.OpSwap && string.Equals(rel, j.Target, StringComparison.OrdinalIgnoreCase))
            {
                string? there = ModVersionFiles.ShaOf(P.Absolute(rel));
                if (there == null || (there.Length > 0 && !string.Equals(there, j.TargetSha, StringComparison.OrdinalIgnoreCase))) continue;
            }

            ModVersionFiles.TryDelete(P.Absolute(rel));
        }

        if (keepActiveCopy)
        {
            // The old manifest plus the kept copy's row (from the new one), so the file is not an orphan.
            var before = ModVersionManifest.Parse(j.Before, out _);
            var after = ModVersionManifest.Parse(j.After, out _);
            var kept = after.Find(j.ModId)?.Parked.Find(p => string.Equals(P.Relative(P.StoredFile(j.ModId, p)), j.ActiveStored, StringComparison.OrdinalIgnoreCase));
            if (kept != null)
            {
                var e = before.GetOrAdd(j.ModId);
                e.Parked.RemoveAll(p => string.Equals(p.Dir, kept.Dir, StringComparison.OrdinalIgnoreCase));
                e.Parked.Add(kept.Clone());
            }

            before.Compact();
            before.Write(P.Manifest);
            ModVersionFiles.TryDelete(P.Journal);
            Info("JoinFlow: versions: recovered an interrupted " + j.Op + " of " + j.ModId + ": undone (the displaced copy stays kept)");
            return "undone " + j.Op + " " + j.ModId;
        }

        WriteManifestJson(j.Before);
        ModVersionFiles.TryDelete(P.Journal);
        Info("JoinFlow: versions: recovered an interrupted " + j.Op + " of " + j.ModId + ": undone");
        return "undone " + j.Op + " " + j.ModId;
    }

    private bool StoredIntact(string rel, string sha)
    {
        if (string.IsNullOrEmpty(rel)) return true;
        string? at = ModVersionFiles.ShaOf(P.Absolute(rel));
        return at != null && string.Equals(at, sha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The committed half of an operation: drop the swapped-in copy from the store, the books, the manifest, the journal.</summary>
    private void FinishLocked(ModVersionJournal j)
    {
        if (j.Op == ModVersionJournal.OpSwap && j.Mode == "now" && j.Stored.Length > 0)
        {
            string stored = P.Absolute(j.Stored);
            ModVersionFiles.TryDelete(stored);
            TryDeleteEmptyDir(Path.GetDirectoryName(stored));
        }

        // A kept copy reused under another file name leaves its old stored file behind.
        foreach (var rel in j.DeleteAfter) ModVersionFiles.TryDelete(P.Absolute(rel));
        Step("finish:files");
        try
        {
            if (j.Active.Length > 0) _env.Books.Remove(j.ModId, j.Active, j.ActiveFile);
            if (j.Op == ModVersionJournal.OpSwap && (j.IncomingLedger != null || j.IncomingRecord != null))
                _env.Books.Put(j.ModId, new ParkedBooks { Ledger = j.IncomingLedger, Record = j.IncomingRecord },
                    j.Target, Path.GetFileName(j.Target), j.TargetSha, j.IncomingSize, pending: j.Mode == "restart");
        }
        catch (Exception e)
        {
            // The books are a record of the files, not the files: a failed row costs a Remove button, never a copy.
            Warn("JoinFlow: versions: the install records could not be updated for " + j.ModId + ": " + e.Message);
        }

        Step("finish:books");
        WriteManifestJson(j.After);
        Step("finish:manifest");
        ModVersionFiles.TryDelete(P.Journal);
    }

    private void WriteManifestJson(string json)
    {
        var m = ModVersionManifest.Parse(json, out _);
        m.Compact();
        m.Write(P.Manifest);
    }

    private static void TryDeleteEmptyDir(string? dir)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir!).Length == 0)
                Directory.Delete(dir!);
        }
        catch { /* an empty folder costs nothing */ }
    }

    // ---- bookkeeping ---------------------------------------------------------------------------

    /// <summary>Record what is active for a mod (a Thunderstore or server copy placed for a server).</summary>
    public void NoteActive(string modId, ActiveVersion? active)
    {
        if (!ModVersionPaths.IsSafeSegment(modId)) return;
        lock (_gate)
        {
            if (JournalPending) return;
            var m = ReadLocked();
            var e = m.GetOrAdd(modId);
            e.Active = active?.Clone();
            m.Compact();
            m.Write(P.Manifest);
        }
    }

    public ModVersionResult SetPreferred(string modId, string version)
    {
        lock (_gate)
        {
            if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
            var m = ReadLocked();
            var e = m.Find(modId);
            if (e == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "Nothing is kept for " + modId + ".");
            e.Preferred = version ?? "";
            m.Write(P.Manifest);
            return ModVersionResult.Done(modId + ": " + (version ?? "") + " is your preferred version.");
        }
    }

    /// <summary>Delete one kept copy. A copy the player installed is only deleted with <paramref name="force"/> (they asked).</summary>
    public ModVersionResult Forget(string modId, string version, bool force)
    {
        lock (_gate)
        {
            if (JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
            var m = ReadLocked();
            var e = m.Find(modId);
            var p = e?.FindParked(version);
            if (e == null || p == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No kept copy of " + modId + " " + version + ".");
            if (p.UserInstalled && !force)
                return ModVersionResult.Refuse(ModVersionCode.Protected, "Your own " + modId + " " + version + " is only deleted when you ask (force).");
            if (e.Pending != null && string.Equals(e.Pending.Version, version, StringComparison.OrdinalIgnoreCase))
                return ModVersionResult.Refuse(ModVersionCode.Queued, modId + " " + version + " is queued for the next start.");
            string file = P.StoredFile(modId, p);
            e.Parked.Remove(p);
            if (string.Equals(e.Preferred, version, StringComparison.OrdinalIgnoreCase)) e.Preferred = "";
            m.Compact();
            m.Write(P.Manifest);
            ModVersionFiles.TryDelete(file);
            TryDeleteEmptyDir(Path.GetDirectoryName(file));
            Info("JoinFlow: versions: deleted the kept " + modId + " " + version);
            return ModVersionResult.Done("Deleted the kept " + modId + " " + version + ".");
        }
    }

    /// <summary>
    /// At Core start: finish an interrupted operation, settle a swap the start promoted (or did not), drop parked rows
    /// whose file is gone, and sweep <c>.part</c> leftovers. Returns one line per change.
    /// </summary>
    public List<string> Reconcile()
    {
        var lines = new List<string>();
        lock (_gate)
        {
            // A swap of this engine that waits for its unload is not a crash: its own completion finishes it
            // (review: a reconcile mid-wait must not write beside a plugin that is still loaded).
            if (_awaitingMod.Length > 0)
            {
                lines.Add("a swap of " + _awaitingMod + " waits for its unload; nothing reconciled");
                return lines;
            }

            string rec = TryRecoverLocked();
            if (rec != "none") lines.Add("journal: " + rec);
            if (JournalPending) return lines;
            int parts = ModVersionFiles.SweepParts(P.Root);
            if (parts > 0) lines.Add("swept " + parts + " .part file(s)");
            var m = ReadLocked();
            bool changed = false;
            foreach (var e in m.Mods)
            {
                if (e.Pending != null)
                {
                    var q = e.Pending;
                    string reloadFile = Path.Combine(P.Reload, q.File);
                    string? at = ModVersionFiles.ShaOf(reloadFile);
                    bool pendingThere = File.Exists(P.Absolute(q.PendingFile));
                    if (at != null && string.Equals(at, q.Sha256, StringComparison.OrdinalIgnoreCase) && !pendingThere)
                    {
                        var p = e.FindParked(q.Version);
                        if (p != null)
                        {
                            string stored = P.StoredFile(e.Id, p);
                            e.Parked.Remove(p);
                            ModVersionFiles.TryDelete(stored);
                            TryDeleteEmptyDir(Path.GetDirectoryName(stored));
                        }

                        e.Active = new ActiveVersion
                        {
                            Version = q.Version, File = q.File, Sha256 = q.Sha256, ForServer = q.ForServer, ForServerName = q.ForServerName,
                            Source = p?.Source ?? ModVersionSource.User,
                        };
                        e.Pending = null;
                        changed = true;
                        lines.Add(e.Id + " " + q.Version + " was promoted at this start");
                    }
                    else if (!pendingThere)
                    {
                        e.Pending = null;
                        changed = true;
                        lines.Add(e.Id + " " + q.Version + " was not promoted (its pending file is gone); the kept copy stays");
                    }
                    else
                    {
                        lines.Add(e.Id + " " + q.Version + " is still in progenitor-pending (the start did not promote it: read the ModManifest promote lines; the allowlist may refuse it)");
                    }
                }

                for (int i = e.Parked.Count - 1; i >= 0; i--)
                {
                    var p = e.Parked[i];
                    if (File.Exists(P.StoredFile(e.Id, p))) continue;
                    lines.Add(e.Id + " " + p.Version + ": the kept file is gone; dropped from the list");
                    e.Parked.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed)
            {
                m.Compact();
                m.Write(P.Manifest);
            }
        }

        foreach (var l in lines) Info("JoinFlow: versions: " + l);
        return lines;
    }

    // ---- bounds ----------------------------------------------------------------------------------

    /// <summary>Apply the bounds now (the bridge and tests). Returns what was dropped.</summary>
    public List<string> Evict()
    {
        lock (_gate) return EvictLocked("", "");
    }

    private List<string> EvictLocked(string keepMod, string keepVersion)
    {
        var dropped = new List<string>();
        try
        {
            if (JournalPending) return dropped;
            var m = ReadLocked();
            var plan = ModVersionBounds.Plan(m, _env.Limits, keepMod, keepVersion);
            if (plan.Count == 0) return dropped;
            var files = new List<string>();
            foreach (var (id, version, dir) in plan)
            {
                var e = m.Find(id);
                if (e == null) continue;
                for (int i = e.Parked.Count - 1; i >= 0; i--)
                {
                    var p = e.Parked[i];
                    if (!string.Equals(p.Dir, dir, StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(P.StoredFile(id, p));
                    e.Parked.RemoveAt(i);
                    dropped.Add(id + " " + version);
                }
            }

            m.Compact();
            m.Write(P.Manifest);
            foreach (var f in files)
            {
                ModVersionFiles.TryDelete(f);
                TryDeleteEmptyDir(Path.GetDirectoryName(f));
            }

            if (dropped.Count > 0) Info("JoinFlow: versions: over the store's bounds; dropped the oldest unused: " + string.Join(", ", dropped));
        }
        catch (Exception e)
        {
            Warn("JoinFlow: versions: applying the bounds threw: " + e.Message);
        }

        return dropped;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void Step(string name) => _env.Fault?.Invoke(name);

    private void Info(string line) => _env.Info?.Invoke(line);

    private void Warn(string line) => _env.Warn?.Invoke(line);
}

/// <summary>Tests: a simulated crash. The engine does not recover from it (a crash does not either).</summary>
internal sealed class FaultException : Exception
{
    public FaultException(string step) : base("fault at " + step) { }
}

/// <summary>JOINFLOW4: the store's bounds: at most N copies per mod and a total size, oldest-unused dropped first.</summary>
internal static class ModVersionBounds
{
    /// <summary>
    /// What to drop, as (mod, version, dir). Never the player's own copies, never a mod's preferred version, never a
    /// copy queued for the next start, never (<paramref name="keepMod"/>, <paramref name="keepVersion"/>) — the copy
    /// the operation just kept. A mod whose copies are all protected may stay over the count.
    /// </summary>
    public static List<(string Id, string Version, string Dir)> Plan(ModVersionManifest m, ModVersionLimits limits, string keepMod, string keepVersion)
    {
        var drop = new List<(string, string, string)>();
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Protected(ModVersionEntry e, ParkedVersion p) =>
            p.UserInstalled
            || string.Equals(e.Preferred, p.Version, StringComparison.OrdinalIgnoreCase)
            || (e.Pending != null && string.Equals(e.Pending.Version, p.Version, StringComparison.OrdinalIgnoreCase))
            || (string.Equals(e.Id, keepMod, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Version, keepVersion, StringComparison.OrdinalIgnoreCase));
        string Key(ModVersionEntry e, ParkedVersion p) => e.Id + "\n" + p.Dir;

        foreach (var e in m.Mods)
        {
            int count = e.Parked.Count;
            if (count <= limits.MaxPerMod) continue;
            var candidates = new List<ParkedVersion>();
            foreach (var p in e.Parked) if (!Protected(e, p)) candidates.Add(p);
            candidates.Sort((a, b) => a.LastUsedUtc.CompareTo(b.LastUsedUtc));
            foreach (var p in candidates)
            {
                if (count <= limits.MaxPerMod) break;
                drop.Add((e.Id, p.Version, p.Dir));
                gone.Add(Key(e, p));
                count--;
            }
        }

        long total = 0;
        foreach (var e in m.Mods) foreach (var p in e.Parked) if (!gone.Contains(Key(e, p))) total += Math.Max(0, p.Size);
        if (total <= limits.MaxTotalBytes) return drop;
        var all = new List<(ModVersionEntry E, ParkedVersion P)>();
        foreach (var e in m.Mods) foreach (var p in e.Parked) if (!gone.Contains(Key(e, p)) && !Protected(e, p)) all.Add((e, p));
        all.Sort((a, b) => a.P.LastUsedUtc.CompareTo(b.P.LastUsedUtc));
        foreach (var (e, p) in all)
        {
            if (total <= limits.MaxTotalBytes) break;
            drop.Add((e.Id, p.Version, p.Dir));
            total -= Math.Max(0, p.Size);
        }

        return drop;
    }
}

/// <summary>JOINFLOW4: <c>progenitor-versions/journal.json</c>: one operation in progress.</summary>
internal sealed class ModVersionJournal
{
    public const string OpPark = "park";
    public const string OpSwap = "swap";

    /// <summary>AUTOUPDATE2 review M3: a queued swap's pending file replaced in place by a newer kept copy.</summary>
    public const string OpRequeue = "requeue";

    public string Op { get; set; } = "";
    public string ModId { get; set; } = "";

    /// <summary>park: <c>move</c> / <c>mark</c>; swap: <c>now</c> / <c>restart</c>.</summary>
    public string Mode { get; set; } = "";

    public string Before { get; set; } = "";
    public string After { get; set; } = "";

    /// <summary>The displaced (active) file, relative to the BepInEx root, its sha256 and name.</summary>
    public string Active { get; set; } = "";
    public string ActiveSha { get; set; } = "";
    public string ActiveFile { get; set; } = "";

    /// <summary>Where the displaced copy is kept (swap), relative.</summary>
    public string ActiveStored { get; set; } = "";

    /// <summary>swap: the file placed, and its sha256 (the committing fact).</summary>
    public string Target { get; set; } = "";
    public string TargetSha { get; set; } = "";

    /// <summary>park: the copy written into the store; swap: the incoming kept copy.</summary>
    public string Stored { get; set; } = "";
    public string StoredSha { get; set; } = "";

    public string Marker { get; set; } = "";
    public List<string> Created { get; set; } = new();

    /// <summary>Files deleted only once the operation commits (a kept copy's old stored name).</summary>
    public List<string> DeleteAfter { get; set; } = new();
    public ParkedLedgerRow? IncomingLedger { get; set; }
    public ParkedRecordRow? IncomingRecord { get; set; }
    public long IncomingSize { get; set; }

    public void Write(string path)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("op", Op);
            w.WriteString("mod", ModId);
            w.WriteString("mode", Mode);
            w.WriteString("active", Active);
            w.WriteString("activeSha", ActiveSha);
            w.WriteString("activeFile", ActiveFile);
            w.WriteString("activeStored", ActiveStored);
            w.WriteString("target", Target);
            w.WriteString("targetSha", TargetSha);
            w.WriteString("stored", Stored);
            w.WriteString("storedSha", StoredSha);
            w.WriteString("marker", Marker);
            w.WriteNumber("incomingSize", IncomingSize);
            w.WriteStartArray("created");
            foreach (var c in Created) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteStartArray("deleteAfter");
            foreach (var c in DeleteAfter) w.WriteStringValue(c);
            w.WriteEndArray();
            // The incoming copy's books ride in a manifest-shaped carrier so one reader parses both.
            var carrier = new ModVersionManifest();
            var e = carrier.GetOrAdd(string.IsNullOrEmpty(ModId) ? "x" : ModId);
            e.Parked.Add(new ParkedVersion { Version = "x", Dir = "x", File = "x.dll", Ledger = IncomingLedger, Record = IncomingRecord });
            w.WriteString("incoming", carrier.ToJson());
            w.WriteString("before", Before);
            w.WriteString("after", After);
            w.WriteEndObject();
        }

        ModVersionFiles.WriteAtomic(path, ms.ToArray());
    }

    public static ModVersionJournal? Read(string path, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "not an object"; return null; }
            var j = new ModVersionJournal
            {
                Op = S(r, "op"),
                ModId = S(r, "mod"),
                Mode = S(r, "mode"),
                Active = S(r, "active"),
                ActiveSha = S(r, "activeSha"),
                ActiveFile = S(r, "activeFile"),
                ActiveStored = S(r, "activeStored"),
                Target = S(r, "target"),
                TargetSha = S(r, "targetSha"),
                Stored = S(r, "stored"),
                StoredSha = S(r, "storedSha"),
                Marker = S(r, "marker"),
                Before = S(r, "before"),
                After = S(r, "after"),
                IncomingSize = r.TryGetProperty("incomingSize", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : 0,
            };
            if (r.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var x in c.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) j.Created.Add(x.GetString() ?? "");
            if (r.TryGetProperty("deleteAfter", out var da) && da.ValueKind == JsonValueKind.Array)
                foreach (var x in da.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) j.DeleteAfter.Add(x.GetString() ?? "");
            var carrier = ModVersionManifest.Parse(S(r, "incoming"), out _);
            if (carrier.Mods.Count > 0 && carrier.Mods[0].Parked.Count > 0)
            {
                j.IncomingLedger = carrier.Mods[0].Parked[0].Ledger;
                j.IncomingRecord = carrier.Mods[0].Parked[0].Record;
            }

            if (j.Op != OpPark && j.Op != OpSwap && j.Op != OpRequeue) { error = "unknown op " + j.Op; return null; }
            return j;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    private static string S(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>JOINFLOW4: the store's sentences. US English.</summary>
internal static class ModVersionText
{
    public static string HashMismatch(string id, string version, string expected, string actual) =>
        "The kept copy of " + id + " " + version + " changed on disk (sha256 " + ModVersionFiles.Short(actual) + ", kept as "
        + ModVersionFiles.Short(expected) + "), so it is not loaded. Install that version again.";

    public static string Allowlist(string id, string version, string file, string why) =>
        id + " " + version + " (" + file + ") is not allowed by your reload allowlist (" + why + "), so it is not loaded.";

    public static string InTheWay(string file, bool pending) =>
        file + " is already in " + (pending ? "progenitor-pending" : "progenitor-reload") + " with other bytes, so it is not replaced.";

    public static string SwappedNote(string version) => version + " is swapped in; it loads within a couple of seconds.";

    public static string AwaitingNote(string version) =>
        version + " is being swapped in: it is written once the previous copy has unloaded (about a second).";

    public static string QueuedNote(string version) => version + " loads at the next start of V Rising.";
}
