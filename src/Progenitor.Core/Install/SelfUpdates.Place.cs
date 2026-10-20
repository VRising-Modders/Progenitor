using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Networking;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// Mod compatibility C4 (docs/mod-compatibility.md, Exact applies to Progenitor itself): a server under <b>Exact version for
/// all</b> holds players to its own Progenitor build. The join screens' <b>Use Progenitor X</b> places that build for the
/// server: from a kept copy with its sha256 (no download), else the release from the signed release list (downloaded, and
/// refused unless its Core is the server's sha256). It is put in place at the title screen through the self-update path:
/// <list type="bullet">
/// <item>a Core whose protocol the running Bootstrap accepts is swapped in place (the running Bootstrap stays: only Core is
/// compared with the server); another protocol stages the build's own Bootstrap and starts at the next launch;</item>
/// <item>automatic Progenitor updates wait while a build is placed (<see cref="PlacementHold"/>);</item>
/// <item>once a join was accepted with it (<see cref="NoteJoinAccepted"/>) and the player is back at the title screen, the
/// copy that ran before is put back, with its self-install record; a placement never used for a join is put back after
/// <see cref="SelfPlacement.MaxUnusedDays"/>.</item>
/// </list>
/// A development build, a mod-manager install or changed files never place (the row says so). Game-free: the probes are the
/// self-update ones.
/// </summary>
internal static partial class SelfUpdates
{
    /// <summary>A Use Progenitor press waiting for its image or for the title screen.</summary>
    private sealed class PlaceRequest
    {
        public ProgenitorRequirement Requirement = new("", "");
        public string Server = "";
        public string Package = "";
        public string Phase = "preparing";
        public SelfImage? Image;
        public string Error = "";
        public DateTime Utc = DateTime.UtcNow;
    }

    private static PlaceRequest? _place;

    /// <summary>
    /// The placement, as the decisions read it (review fix): a damaged file is moved aside and logged, and the placement of
    /// the build that runs now is recovered from the copy kept beside that build, so the player's own copy (not the placed
    /// build) is still the one given back.
    /// </summary>
    internal static SelfPlacement? LoadPlacement(string root, string runningCoreSha)
    {
        var p = SelfPlacedStore.ReadChecked(root, out string damaged);
        if (damaged.Length == 0) return p;
        var recovered = SelfPlacedStore.Recover(root, runningCoreSha);
        if (recovered != null && SelfPlacedStore.Write(root, recovered, out _))
        {
            Write("op=placed result=\"the placement file was damaged (moved to " + damaged + "); recovered the placement of Progenitor "
                + recovered.Version + " for " + ServerWords(recovered.Server) + "\"");
            return recovered;
        }

        Write("op=placed result=\"the placement file was damaged (moved to " + damaged + "); the running build was not placed for a server\"");
        return null;
    }

    /// <summary>A restore was tried during this title-screen visit (cleared when the apply window closes).</summary>
    private static bool _restoreTriedThisVisit;

    /// <summary>The journal is the restore of a build placed for a server (its new Core is the player's own copy).</summary>
    internal static bool IsRestoreJournal(string root, SelfJournal j)
    {
        var p = SelfPlacedStore.Read(root);
        return p != null && p.State == SelfPlacement.Restoring && string.Equals(p.PreviousCoreSha, j.NewCore, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The joined server's Progenitor requirement while connected (live: its verified catalog), or null. A click on Update
    /// for another build is refused while it is set.
    /// </summary>
    internal static Func<ProgenitorRequirement?>? ConnectedRequirement { get; set; }

    private static bool IsServer()
    {
        try { return IsServerProbe(); }
        catch { return false; }
    }

    /// <summary>The Progenitor releases of the index this process reads (empty before it was read).</summary>
    public static IReadOnlyList<SelfRelease> Releases
    {
        get { lock (Gate) return _releases; }
    }

    /// <summary>Where the server's build can come from (the join screens' row): a kept copy, the index, or nowhere.</summary>
    public static ProgenitorExact.FixSource SourceOf(ProgenitorRequirement req)
    {
        if (req == null || !req.Valid) return ProgenitorExact.FixSource.None;
        string? root = Root();
        if (!string.IsNullOrEmpty(root) && SelfStore.Select(SelfStore.List(root!), req.Version, req.Sha) != null)
            return ProgenitorExact.FixSource.Kept;
        if (ListedRelease(req.Version) != null) return ProgenitorExact.FixSource.Thunderstore;
        // Review fix: the index this client read may be old or never read; the server says the build is published, so read
        // it once (a conditional request with the update check's backoff). The join screens redraw when it lands.
        AskIndexFor(req.Version);
        return ProgenitorExact.FixSource.None;
    }

    private static readonly HashSet<string> IndexAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised (on the main thread) when the Progenitor releases of the index were read again; the join screens rebuild
    /// their Progenitor row (live: <c>JoinFlowClient.MarkVersionsDirty</c>).
    /// </summary>
    internal static Action? ReleasesRead { get; set; }

    /// <summary>One index check per required release and session (client only), when the release is not listed yet.</summary>
    private static void AskIndexFor(string version)
    {
        if (IsServer() || CheckIndex == null) return;
        lock (Gate)
            if (!IndexAsked.Add(version ?? "")) return;
        Write("op=place-index version=" + version + " (a server requires it and the index here does not list it; reading the index)");
        try { _ = CheckNow(); }
        catch (Exception e) { Write("op=place-index result=\"failed: " + e.Message + "\""); }
    }

    private static SelfRelease? ListedRelease(string version)
    {
        foreach (var r in Releases)
            if (r != null && r.Active && r.DownloadUrl.Length > 0 && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>The version a placement is being prepared, committed or swapped in for, or empty.</summary>
    public static string PlacingVersion
    {
        get
        {
            lock (Gate) if (_place != null && _place.Phase != "failed") return _place.Requirement.Version;
            string? root = Root();
            var p = string.IsNullOrEmpty(root) ? null : SelfPlacedStore.Read(root!);
            return p?.State == SelfPlacement.Placing ? p.Version : "";
        }
    }

    private static ProgenitorExact.ClientFacts? _clientFacts;
    private static double _clientFactsAt = double.MinValue;
    private static int _clientFactsVersion = -1;

    /// <summary>
    /// The join screens' facts about this client's own Progenitor (rebuilt at most every <see cref="FactsSeconds"/>, or when
    /// a placement or an update moved <see cref="Version"/>; the join screens rebuild their model often).
    /// </summary>
    public static ProgenitorExact.ClientFacts ClientFacts()
    {
        double now = Clock();
        lock (Gate)
            if (_clientFacts != null && _clientFactsVersion == _version && now >= _clientFactsAt && now - _clientFactsAt < FactsSeconds)
                return _clientFacts;
        var f = Facts();
        var kept = new Dictionary<string, ProgenitorExact.FixSource>(StringComparer.OrdinalIgnoreCase);
        var facts = new ProgenitorExact.ClientFacts
        {
            CoreVersion = f.CoreVersion,
            CoreSha = f.CoreSha,
            Kind = SelfDetection.KindOf(f),
            Placing = PlacingVersion,
        };
        // The source is looked up once per requirement within the cache's life (the store is listed from disk).
        facts.SourceOf = req =>
        {
            string key = req.Version + "|" + req.Sha;
            lock (kept)
            {
                if (kept.TryGetValue(key, out var s)) return s;
                s = SourceOf(req);
                kept[key] = s;
                return s;
            }
        };
        lock (Gate)
        {
            _clientFacts = facts;
            _clientFactsAt = now;
            _clientFactsVersion = _version;
        }

        return facts;
    }

    /// <summary>Why automatic updates (and a click on Update) wait for a build placed for a server, or empty.</summary>
    internal static string PlacementHold()
    {
        lock (Gate)
            if (_place != null && _place.Phase != "failed")
                return "held: Progenitor " + _place.Requirement.Version + " is being put in place for " + ServerWords(_place.Server);
        string? root = Root();
        var p = string.IsNullOrEmpty(root) ? null : SelfPlacedStore.Read(root!);
        if (p == null) return "";
        return "held: Progenitor " + p.Version + " is in place for " + ServerWords(p.Server) + " until you leave it";
    }

    private static string ServerWords(string server) => string.IsNullOrWhiteSpace(server) ? "a server" : server.Trim();

    /// <summary>Why a click on Update must not install <paramref name="version"/> while connected, or empty.</summary>
    internal static string ConnectedExactReason(string version)
    {
        ProgenitorRequirement? req;
        try { req = ConnectedRequirement?.Invoke(); }
        catch { req = null; }
        if (req == null || string.Equals(req.Version, version, StringComparison.OrdinalIgnoreCase)) return "";
        return "not now: the server you are on requires exactly Progenitor " + req.Version;
    }

    /// <summary>
    /// <b>Use Progenitor X</b>: prepare the server's build and put it in place at the title screen. False with the reason when
    /// it cannot be placed from here; true with what happens next.
    /// </summary>
    public static bool Place(ProgenitorRequirement req, string server, out string message)
    {
        message = "";
        if (req == null || !req.Valid) { message = "The server did not name a Progenitor build."; return false; }
        if (IsServer()) { message = "A server never places another Progenitor build."; return false; }
        string? root = Root();
        if (string.IsNullOrEmpty(root)) { message = "Progenitor's folder could not be found."; return false; }
        var facts = Facts();
        if (string.Equals(facts.CoreSha, req.Sha, StringComparison.OrdinalIgnoreCase)) { message = "Progenitor " + req.Version + " already runs here; join again."; return true; }
        switch (SelfDetection.KindOf(facts))
        {
            case SelfInstallKind.Development: message = "Can't use Progenitor " + req.Version + ": " + ProgenitorExact.NoteDevelopment + "."; return false;
            case SelfInstallKind.ModManager: message = "Can't use Progenitor " + req.Version + ": " + ProgenitorExact.NoteModManager(req.Version) + "."; return false;
            case SelfInstallKind.Changed: message = "Can't use Progenitor " + req.Version + ": " + ProgenitorExact.NoteChanged + "."; return false;
        }

        if (facts.State.Journal is { InProgress: true } busy) { message = "A Progenitor update is in progress (" + busy.State + "); try again when it is done."; return false; }
        var existing = LoadPlacement(root!, facts.CoreSha);
        if (existing != null && string.Equals(existing.CoreSha, req.Sha, StringComparison.OrdinalIgnoreCase))
        {
            message = "Progenitor " + req.Version + " is already being put in place for " + ServerWords(existing.Server) + ".";
            return true;
        }

        if (existing != null && existing.State != SelfPlacement.Placed)
        {
            message = "Progenitor is still busy with the build placed for " + ServerWords(existing.Server) + " (" + existing.State + "); try again in a moment.";
            return false;
        }

        var request = new PlaceRequest { Requirement = req, Server = server ?? "" };
        string droppedClick = "";
        lock (Gate)
        {
            if (_running) { message = "An update of Progenitor is running; try again when it is done."; return false; }
            if (_place != null && _place.Phase != "failed" && !string.Equals(_place.Requirement.Sha, req.Sha, StringComparison.OrdinalIgnoreCase))
            {
                message = "Progenitor " + _place.Requirement.Version + " is already being put in place; try again when it is done.";
                return false;
            }

            if (_place != null && _place.Phase != "failed")
            {
                message = "Progenitor " + req.Version + " is already being put in place.";
                return true;
            }

            _place = request;
            _version++;
            // Review fix: a click deferred before the placement was for the player's own line; it is not installed after
            // the player's copy comes back (they click Update again).
            if (_deferredClick.Length > 0)
            {
                droppedClick = _deferredClick;
                _deferredClick = "";
            }
        }

        if (droppedClick.Length > 0)
            Write("op=deferred version=" + droppedClick + " result=dropped why=\"a build is being placed for a server\"");

        var kept = SelfStore.Select(SelfStore.List(root!), req.Version, req.Sha);
        if (kept != null)
        {
            var image = SelfImageReader.ReadKept(System.IO.Path.Combine(SelfPaths.Store(root!), kept.Dir));
            request.Package = kept.Package;
            Prepared(request, image, "kept " + kept.Dir);
        }
        else
        {
            var release = ListedRelease(req.Version);
            if (release == null || Download == null)
            {
                Prepared(request, new SelfImage { Error = ProgenitorExact.NoteNotListed(req.Version) }, "");
            }
            else if (release.CoreSha.Length > 0 && !string.Equals(release.CoreSha, req.Sha, StringComparison.OrdinalIgnoreCase))
            {
                // The release list names the release's Core: another build is known not to be the server's, with no download.
                Prepared(request, new SelfImage { Error = "Progenitor " + req.Version + " from the release list is not this server's build (its Core differs); tell the server's admin" }, "");
            }
            else
            {
                request.Package = release.Package;
                Write("op=place-download version=" + req.Version + " for=\"" + request.Server + "\" url=" + release.DownloadUrl);
                long max = release.Size > 0 ? release.Size + 1024 : NonModPackages.DownloadLimit;
                CancellationToken token;
                lock (Gate) token = _cancel.Token;
                Task<(byte[]? Bytes, string Error)> download;
                try { download = Download(release.DownloadUrl, Math.Min(max, NonModPackages.DownloadLimit), token); }
                catch (Exception e) { download = Task.FromResult<(byte[]?, string)>((null, e.Message)); }
                download.ContinueWith(t =>
                {
                    SelfImage image;
                    if (t.Status != TaskStatus.RanToCompletion) image = new SelfImage { Error = "download: " + (t.Exception?.GetBaseException().Message ?? "failed") };
                    else if (t.Result.Bytes == null) image = new SelfImage { Error = "download: " + (t.Result.Error.Length > 0 ? t.Result.Error : "failed") };
                    else if (SelfFeed.CheckDownload(release, t.Result.Bytes) is { Length: > 0 } bad) image = new SelfImage { Error = "download: " + bad };
                    else image = SelfImageReader.Read(t.Result.Bytes);
                    if (token.IsCancellationRequested) return;
                    PostMain(() => Prepared(request, image, "release"));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        if (request.Phase == "failed")
        {
            message = "Can't use Progenitor " + req.Version + ": " + request.Error + ".";
            return false;
        }

        message = ProgenitorExact.Queued(req.Version, SafeWindow().Length == 0 || IsTitleOnlyClosed());
        return true;
    }

    /// <summary>True when the apply window is closed only by the join screens (the refusal), which close on the press.</summary>
    private static bool IsTitleOnlyClosed()
    {
        string w = SafeWindow();
        return w.Length == 0 || w.IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The image of a placement is read: check it is the server's build, then wait for the title screen.</summary>
    private static void Prepared(PlaceRequest request, SelfImage image, string from)
    {
        string error = "";
        if (image == null || !image.Ok) error = image?.Error is { Length: > 0 } e ? e : "the build could not be read";
        else if (!string.Equals(image.CoreSha, request.Requirement.Sha, StringComparison.OrdinalIgnoreCase))
            error = "Progenitor " + request.Requirement.Version + " " + (from == "release" ? "from the release list" : "kept here") + " is not this server's build (its Core differs); tell the server's admin";
        lock (Gate)
        {
            if (!ReferenceEquals(_place, request)) return;
            if (error.Length > 0)
            {
                request.Phase = "failed";
                request.Error = error;
            }
            else
            {
                request.Phase = "ready";
                request.Image = image;
            }

            _version++;
        }

        if (error.Length > 0)
        {
            Write("op=place version=" + request.Requirement.Version + " result=\"failed: " + error + "\"");
            try { Notice?.Invoke(0, 1, "Can't use Progenitor " + request.Requirement.Version + ": " + error + "."); }
            catch { /* */ }
        }
        else Write("op=place version=" + request.Requirement.Version + " result=ready from=" + from + " (waits for the title screen)");
    }

    /// <summary>Main thread, from Handshake: a join was accepted. A placed build that runs now has been used.</summary>
    public static void NoteJoinAccepted()
    {
        if (IsServer()) return;
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return;
        var p = SelfPlacedStore.Read(root!);
        if (p == null || p.Joined || p.State == SelfPlacement.Restoring) return;
        if (!string.Equals(Facts().CoreSha, p.CoreSha, StringComparison.OrdinalIgnoreCase)) return;
        p.Joined = true;
        p.State = SelfPlacement.Placed;
        if (SelfPlacedStore.Write(root!, p, out _))
            Write("op=placed-used version=" + p.Version + " for=\"" + p.Server + "\" (your own Progenitor comes back at the title screen after you leave)");
    }

    /// <summary>The placement half of the tick. Returns what it did, or empty to go on with automatic updates.</summary>
    private static string TickPlacement(string root)
    {
        PlaceRequest? ready;
        lock (Gate) ready = _place is { Phase: "ready" } r ? r : null;
        // Nothing placed and nothing prepared (the usual tick): no facts are read.
        if (ready == null && !System.IO.File.Exists(SelfPlacedStore.PathOf(root))) return "";
        var facts = Facts();
        bool journalBusy = facts.State.Journal is { InProgress: true };
        if (ready != null)
        {
            string closed = SafeWindow();
            if (closed.Length > 0 || journalBusy || Busy) return "placement waits: " + (closed.Length > 0 ? closed : "an update is in progress");
            string result;
            try { result = CommitPlacement(root, ready, facts); }
            catch (Exception e) { result = "failed: " + e.Message; }
            lock (Gate)
            {
                if (ReferenceEquals(_place, ready)) _place = null;
                _version++;
            }

            Write("op=place version=" + ready.Requirement.Version + " result=\"" + result + "\"");
            Invalidate();
            return "placement: " + result;
        }

        var p = LoadPlacement(root, facts.CoreSha);
        if (p == null) return "";
        string running = facts.CoreSha;
        bool isPlaced = string.Equals(running, p.CoreSha, StringComparison.OrdinalIgnoreCase);
        bool isPrevious = string.Equals(running, p.PreviousCoreSha, StringComparison.OrdinalIgnoreCase);
        switch (p.State)
        {
            case SelfPlacement.Placing:
                if (isPlaced)
                {
                    p.State = SelfPlacement.Placed;
                    SelfPlacedStore.Write(root, p, out _);
                    Write("op=placed version=" + p.Version + " for=\"" + p.Server + "\"");
                    try { Notice?.Invoke(1, 0, ProgenitorExact.PlacedNotice(p.Version)); }
                    catch { /* */ }
                    Bump();
                    return "placed";
                }

                if (!journalBusy)
                {
                    Drop(root, p, running, "the swap did not load it");
                    return "placement dropped";
                }

                return "placement waits for its swap";
            case SelfPlacement.Placed:
                if (!isPlaced)
                {
                    if (journalBusy) return "";
                    // Another build runs (a rollback, an update installed by hand, a developer deploy): the placement is over.
                    Drop(root, p, running, "another Progenitor build runs now");
                    return "";
                }

                if (!p.RestoreDue(SafeUtcNow())) return "";
                // Review fix: at most one restore per title-screen visit, so a refusal that lasts the process (a plugin
                // pinning Core) does not spend every try within seconds. A closed window (a join, a world) starts a new visit.
                if (SafeWindow().Length > 0)
                {
                    lock (Gate) _restoreTriedThisVisit = false;
                    return "";
                }

                if (journalBusy || Busy) return "";
                lock (Gate)
                {
                    if (_restoreTriedThisVisit) return "";
                    _restoreTriedThisVisit = true;
                }

                string restore;
                try { restore = BeginRestore(root, p, facts); }
                catch (Exception e) { restore = "failed: " + e.Message; }
                Write("op=restore version=" + p.PreviousVersion + " after=\"" + p.Server + "\" result=\"" + restore + "\"");
                Invalidate();
                return "restore: " + restore;
            case SelfPlacement.Restoring:
                if (journalBusy) return "restore waits for its swap";
                if (isPrevious)
                {
                    // After the trial's healthy mark (which recorded the release): the record as it was before the placement.
                    if (p.PreviousRecord != null) SelfFiles.WriteRecord(root, p.PreviousRecord, out _);
                    else
                    {
                        try { System.IO.File.Delete(SelfPaths.Record(root)); }
                        catch { /* a stale record reads as changed files, never as managed */ }
                    }

                    SelfPlacedStore.Delete(root);
                    Invalidate();
                    Write("op=restored version=" + p.PreviousVersion + " after=\"" + p.Server + "\"");
                    try { Notice?.Invoke(1, 0, ProgenitorExact.RestoredNotice(p.PreviousVersion)); }
                    catch { /* */ }
                    Recompute("restored");
                    return "restored";
                }

                // Review fix: a swap that did not run (the journal superseded, the placed build still runs) is tried again
                // at the next title screen, a few times; a failed one (or too many tries) gives the restore up.
                if (isPlaced && facts.State.Journal?.State == SelfJournal.Superseded && p.RestoreAttempts + 1 < SelfPlacement.MaxRestoreAttempts)
                {
                    p.State = SelfPlacement.Placed;
                    p.RestoreAttempts++;
                    SelfPlacedStore.Write(root, p, out _);
                    Write("op=restore version=" + p.PreviousVersion + " result=\"not swapped; tried again (" + p.RestoreAttempts + " of " + SelfPlacement.MaxRestoreAttempts + ")\"");
                    Bump();
                    return "restore will be tried again";
                }

                GiveUp(root, p, "the swap did not bring it back");
                return "restore failed";
        }

        return "";
    }

    /// <summary>
    /// The placement is over without a restore: the build placed before it for another server comes back as the placement
    /// when that is what runs now (a chained placement that did not load), else the file goes.
    /// </summary>
    private static void Drop(string root, SelfPlacement p, string running, string why)
    {
        var earlier = p.Earlier();
        if (earlier != null && string.Equals(running, earlier.CoreSha, StringComparison.OrdinalIgnoreCase))
        {
            try { System.IO.File.Delete(SelfPlacedStore.MarkerOf(root, p)); }
            catch { /* only used for recovery, and only for the build that runs */ }
            SelfPlacedStore.Write(root, earlier, out _);
            Write("op=placed version=" + p.Version + " result=\"dropped: " + why + "; Progenitor " + earlier.Version + " stays placed for " + ServerWords(earlier.Server) + "\"");
        }
        else
        {
            SelfPlacedStore.Delete(root);
            Write("op=placed version=" + p.Version + " result=\"dropped: " + why + "\"");
        }

        Bump();
    }

    /// <summary>The player's own build could not be put back: the placement ends and the running build stays.</summary>
    private static void GiveUp(string root, SelfPlacement p, string why)
    {
        SelfPlacedStore.Delete(root);
        // Review fix: the player's own version is not a failed release (a failed restore swap, or Bootstrap's rollback of a
        // restore, listed it): it stays eligible for updates.
        SelfFiles.EditState(root, s => s.Failed.RemoveAll(f => string.Equals(f.Version, p.PreviousVersion, StringComparison.OrdinalIgnoreCase)) > 0, out _);
        Write("op=restore version=" + p.PreviousVersion + " result=\"failed: " + why + "; Progenitor " + p.Version + " stays; Update Progenitor applies as usual\"");
        try { Notice?.Invoke(0, 1, "Your Progenitor " + p.PreviousVersion + " could not be put back (" + why + "); Progenitor " + p.Version + " stays."); }
        catch { /* */ }
        Bump();
    }

    /// <summary>Main thread, the window open: keep both copies, write the placement and the journal, stage and swap.</summary>
    private static string CommitPlacement(string root, PlaceRequest r, SelfFacts facts)
    {
        var image = r.Image!;
        int protocol = BootstrapLinkProtocol();
        var (route, why) = SelfRoute.Placement(image, facts.CoreSha, protocol);
        Write("op=place-route version=" + r.Requirement.Version + " route=" + route + " why=\"" + why + "\"");
        if (route == SelfRouteKind.Refused)
        {
            try { Notice?.Invoke(0, 1, "Can't use Progenitor " + r.Requirement.Version + ": " + why + "."); }
            catch { /* */ }
            return "refused: " + why;
        }

        if (route == SelfRouteKind.Current) return "current: it already runs";
        // Review fix: as for an update, the running Bootstrap must report its image (the record and a rollback need it).
        if (facts.BootstrapSha.Length == 0) return "not now: the running Bootstrap does not report its image (restart once with this Bootstrap)";

        string runningDir = KeepRunning(root, facts, route, out string keepError);
        if (runningDir.Length == 0) return "failed: the running copy could not be kept (" + keepError + "); nothing was changed";
        string newDir = SelfStore.Keep(root, new SelfKept
        {
            Version = r.Requirement.Version,
            Package = r.Package,
            CoreSha = image.CoreSha,
            BootstrapSha = image.BootstrapSha,
            Protocol = image.CoreProtocol,
            Source = SelfFeed.KeptSource,
        }, image.Core, image.Bootstrap, image.Tables, out string newError);
        if (newDir.Length == 0) return "failed: the build could not be kept (" + newError + "); nothing was changed";

        if (!Allowed(SelfPaths.CoreFile, image.CoreSha) || (route == SelfRouteKind.Restart && !Allowed(SelfPaths.BootstrapFile, image.BootstrapSha))
            // A rollback, and the restore after leaving, put the running copy back through the same gate.
            || !Allowed(SelfPaths.CoreFile, facts.CoreSha) || (route == SelfRouteKind.Restart && !Allowed(SelfPaths.BootstrapFile, facts.BootstrapSha)))
            return "refused: its sha256 (or the running copy's) is not in BepInEx/config/progenitor-allow.txt";

        // A build placed for another server earlier keeps the player's own copy as the one to give back.
        var earlier = LoadPlacement(root, facts.CoreSha);
        string installed = SelfDetection.InstalledOf(facts, SelfDetection.KindOf(facts));
        var placement = new SelfPlacement
        {
            State = SelfPlacement.Placing,
            Server = r.Server,
            Version = r.Requirement.Version,
            CoreSha = image.CoreSha,
            PreviousVersion = earlier?.PreviousVersion ?? installed,
            PreviousCoreSha = earlier?.PreviousCoreSha ?? facts.CoreSha,
            PreviousBootstrapSha = earlier?.PreviousBootstrapSha ?? facts.BootstrapSha,
            PreviousDir = earlier?.PreviousDir ?? runningDir,
            PreviousRecord = earlier != null ? earlier.PreviousRecord : facts.Record,
            PlacedUtc = SafeUtcNow(),
        };
        // Review fix: a chained placement remembers the one it replaces, which comes back if this one never loads.
        if (earlier != null) placement.RememberEarlier(earlier);
        if (!SelfPlacedStore.Write(root, placement, out string perr)) return "failed: the placement could not be written (" + perr + "); nothing was changed";

        var journal = new SelfJournal
        {
            Version = r.Requirement.Version,
            Previous = installed,
            Package = r.Package,
            NewDir = newDir,
            PrevDir = runningDir,
            NewCore = image.CoreSha,
            NewBootstrap = route == SelfRouteKind.Restart ? image.BootstrapSha : "",
            PrevCore = facts.CoreSha,
            PrevBootstrap = facts.BootstrapSha,
            Route = route == SelfRouteKind.Hot ? "hot" : "restart",
            Utc = DateTime.UtcNow,
        };

        string staged = Stage(root, journal, image, route, "self-update " + r.Requirement.Version + " (placed for " + ServerWords(r.Server) + ")");
        if (!staged.StartsWith("ok", StringComparison.Ordinal))
        {
            if (earlier != null) SelfPlacedStore.Write(root, earlier, out _);
            else SelfPlacedStore.Delete(root);
            return staged;
        }

        if (route == SelfRouteKind.Restart)
        {
            try { Notice?.Invoke(1, 0, "Progenitor " + r.Requirement.Version + " starts at the next launch. Restart V Rising, then join " + ServerWords(r.Server) + " again."); }
            catch { /* */ }
            return "staged: Progenitor " + r.Requirement.Version + " starts at the next launch (" + why + ")";
        }

        return "swap scheduled: " + staged.Substring(2).Trim();
    }

    /// <summary>Main thread, the window open: put the copy that ran before the placement back.</summary>
    private static string BeginRestore(string root, SelfPlacement p, SelfFacts facts)
    {
        var image = SelfImageReader.ReadKept(SelfPaths.Absolute(root, p.PreviousDir));
        if (!image.Ok || !string.Equals(image.CoreSha, p.PreviousCoreSha, StringComparison.OrdinalIgnoreCase))
        {
            SelfPlacedStore.Delete(root);
            return "dropped: the copy that ran before is gone (" + (image.Ok ? "another build is kept there" : image.Error) + "); Progenitor " + p.Version + " stays";
        }

        int protocol = BootstrapLinkProtocol();
        var (route, why) = SelfRoute.Placement(image, facts.CoreSha, protocol);
        // The player's own Bootstrap comes back too when the placement replaced it (the restart route).
        if (route == SelfRouteKind.Hot && p.PreviousBootstrapSha.Length > 0 && facts.BootstrapSha.Length > 0
            && !string.Equals(p.PreviousBootstrapSha, facts.BootstrapSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(image.BootstrapSha, p.PreviousBootstrapSha, StringComparison.OrdinalIgnoreCase)
            && image.BootstrapProtocol == image.CoreProtocol)
        {
            route = SelfRouteKind.Restart;
            why = "your own Bootstrap comes back too: both start at the next launch";
        }

        if (route == SelfRouteKind.Refused)
        {
            SelfPlacedStore.Delete(root);
            return "dropped: " + why + "; Progenitor " + p.Version + " stays";
        }

        // Review fix: the restore goes through the allowlist like any update.
        if (route != SelfRouteKind.Current && (!Allowed(SelfPaths.CoreFile, image.CoreSha) || (route == SelfRouteKind.Restart && !Allowed(SelfPaths.BootstrapFile, image.BootstrapSha))))
        {
            SelfPlacedStore.Delete(root);
            return "dropped: your own copy's sha256 is not in BepInEx/config/progenitor-allow.txt; Progenitor " + p.Version + " stays";
        }

        if (route == SelfRouteKind.Current)
        {
            p.State = SelfPlacement.Restoring;
            SelfPlacedStore.Write(root, p, out _);
            return "current: your own Progenitor already runs";
        }

        string placedDir = KeepRunning(root, facts, route, out string keepError);
        if (placedDir.Length == 0) return "not now: the running copy could not be kept (" + keepError + ")";
        var journal = new SelfJournal
        {
            Version = p.PreviousVersion,
            Previous = p.Version,
            Package = p.PreviousRecord?.Package ?? "",
            NewDir = p.PreviousDir,
            PrevDir = placedDir,
            NewCore = image.CoreSha,
            NewBootstrap = route == SelfRouteKind.Restart ? image.BootstrapSha : "",
            PrevCore = facts.CoreSha,
            PrevBootstrap = facts.BootstrapSha,
            Route = route == SelfRouteKind.Hot ? "hot" : "restart",
            Utc = DateTime.UtcNow,
        };
        p.State = SelfPlacement.Restoring;
        if (!SelfPlacedStore.Write(root, p, out string perr)) return "not now: the placement could not be written (" + perr + ")";
        string staged = Stage(root, journal, image, route, "self-update " + p.PreviousVersion + " (yours again after leaving " + ServerWords(p.Server) + ")");
        if (!staged.StartsWith("ok", StringComparison.Ordinal))
        {
            // Tried again at the next title screen, a few times (some refusals last the whole process).
            p.State = SelfPlacement.Placed;
            p.RestoreAttempts++;
            if (p.RestoreAttempts >= SelfPlacement.MaxRestoreAttempts)
            {
                GiveUp(root, p, staged);
                return staged + " (given up)";
            }

            SelfPlacedStore.Write(root, p, out _);
            return staged;
        }

        if (route == SelfRouteKind.Restart)
        {
            try { Notice?.Invoke(1, 0, "Your Progenitor " + p.PreviousVersion + " comes back at the next launch."); }
            catch { /* */ }
            return "staged: your Progenitor " + p.PreviousVersion + " starts at the next launch (" + why + ")";
        }

        return "swap scheduled: " + staged.Substring(2).Trim();
    }

    /// <summary>Write the journal and stage the image: a Core swap (hot) or a Bootstrap for the next launch. <c>ok …</c> or the failure.</summary>
    private static string Stage(string root, SelfJournal journal, SelfImage image, SelfRouteKind route, string reason)
    {
        if (route == SelfRouteKind.Hot)
        {
            journal.State = SelfJournal.Swapping;
            journal.Starts = 1;
            if (!SaveJournal(root, journal, out string jerr)) return "failed: the journal could not be written (" + jerr + "); nothing was changed";
            if (!SelfFiles.WriteAtomic(SelfPaths.CoreNext(root), image.Core, out string werr))
            {
                ClearJournal(root, "the staged Core could not be written: " + werr);
                return "failed: " + werr;
            }

            SelfFiles.CopyTables(SelfPaths.Absolute(root, journal.NewDir), SelfPaths.Tables(root));
            string answer;
            try { answer = RequestSwap?.Invoke(reason) ?? "no swap seam"; }
            catch (Exception e) { answer = "threw: " + e.Message; }
            if (!answer.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase))
            {
                UndoStagedCore(root, image.CoreSha);
                ClearJournal(root, "the swap was not scheduled: " + answer);
                return "not swapped: " + answer;
            }

            return "ok " + answer;
        }

        string bootPath = SafeBootstrapPath(root);
        journal.State = SelfJournal.Staged;
        journal.Starts = 0;
        if (!SaveJournal(root, journal, out string sjerr)) return "failed: the journal could not be written (" + sjerr + "); nothing was changed";
        if (!SelfFiles.WriteAtomic(bootPath + SelfPaths.NextSuffix, image.Bootstrap, out string berr))
        {
            ClearJournal(root, "the staged Bootstrap could not be written: " + berr);
            return "failed: " + berr;
        }

        return "ok staged";
    }

    private static Dictionary<string, object?>? DescribePlacing()
    {
        lock (Gate)
        {
            if (_place == null) return null;
            return new Dictionary<string, object?>
            {
                ["version"] = _place.Requirement.Version,
                ["coreSha256"] = _place.Requirement.Sha,
                ["server"] = _place.Server,
                ["phase"] = _place.Phase,
                ["error"] = _place.Error,
            };
        }
    }

    /// <summary>Bridge and tests: forget a placement being prepared (a failed one is replaced by the next press anyway).</summary>
    public static bool ForgetPlacing()
    {
        lock (Gate)
        {
            IndexAsked.Clear();
            if (_place == null) return false;
            _place = null;
            _version++;
            return true;
        }
    }
}
