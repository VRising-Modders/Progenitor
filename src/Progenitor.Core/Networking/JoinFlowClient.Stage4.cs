using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>JOINFLOW4: the join screens' side of keeping both versions (Use kept X, the readback, the idle rule).</summary>
internal static partial class JoinFlowClient
{
    /// <summary>The joined server as the screens name it.</summary>
    internal static string ServerLabelNow
    {
        get { lock (Gate) return JoinText.ServerLabel(ServerNameLocked(), _host); }
    }

    /// <summary>
    /// The joined server's name as this client knows it now (empty when unknown), when <paramref name="host"/> is this
    /// join's; else empty. The screens' titles re-read it: the name can become known after the page was built.
    /// </summary>
    internal static string ServerNameNowFor(string? host)
    {
        lock (Gate)
        {
            if (!string.Equals((host ?? "").Trim(), (_host ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return "";
            return ServerNameLocked() ?? "";
        }
    }

    /// <summary>
    /// Something of the join flow is under way, so the version store must not swap back yet: the screens are open (the
    /// refusal screens included), a Thunderstore batch runs, a reconnect is in flight, or a fresh rejoin record waits.
    /// </summary>
    internal static bool BusyForVersions
    {
        get
        {
            lock (Gate)
            {
                if (_session != null && !_session.Closed) return true;
                if (_batch != null && !_batch.Done) return true;
                if (_reconnect != null) return true;
            }

            try
            {
                var record = RejoinStore.Peek(RejoinPath);
                if (record != null && !record.DryRun && RejoinStore.Judge(record, DateTime.UtcNow) == RejoinPickup.Rejoin) return true;
            }
            catch { /* no record */ }

            return false;
        }
    }

    /// <summary>Answer the handshake again on this connection (live: <c>Handshake.ReDeclareSelf</c>; tests: a stand-in).</summary>
    internal static Func<bool> ReAnswerHandshake { get; set; } =
#if PROGENITOR_TESTS
        () => false;
#else
        Handshake.ReDeclareSelf;
#endif

    /// <summary>
    /// 2026-10-05: whether this client can fix the server's asked row <paramref name="guid"/> by itself during the join, so
    /// its answer may declare it (the install window's local fix): a copy the player turned off that hot-loads and passes
    /// the server's check (Turn on, now), or a kept copy the server's check accepts that swaps in now (Use kept). A restart
    /// route fixes nothing inside the join, so it is not declared. Never throws.
    /// </summary>
    internal static bool CanFixLocally(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        try
        {
            IReadOnlyList<Handshake.ModRecord> asked = Handshake.CopyServerAsked();
            IReadOnlyList<Handshake.ModRecord> provided = Handshake.CopyProvided();
            var off = SafeOffCopy(guid);
            if (off != null && !off.PausedHere && off.AppliesNow && !off.TurnsOnAtNextStart
                && JoinPageModel.OffCopySatisfies(guid, off, asked, provided))
                return true;
            // SELFFIX1: with [Install] LocalIndexPath set, a hot-loadable package of the local test source at a version the
            // server's own check accepts (claimed only: the server re-judges the answer when the window ends).
            if (LocalTestMap().TryGetValue(guid, out var local) && local.HotLoadable
                && LocalTestVersionAccepted(guid, asked, provided, local) != null)
                return true;
            if (ModVersionJoin.Service == null) return false;
            var kept = ModVersionJoin.KeptFor(guid, asked, provided);
            return kept is { PickUsable: true } && kept.PickRoute == "now";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"Use kept X": swap the kept copy in through the store; the Result page says what comes next.</summary>
    private static string UseKeptLive(JoinRequiredRow row, out JoinResultOffer? offer)
    {
        offer = null;
        var svc = ModVersionJoin.Service;
        var kept = row?.Kept;
        if (svc == null || row == null || kept == null || !kept.HasPick) return "Keeping versions is not available here.";
        JoinMode mode;
        JoinTarget target;
        string name;
        lock (Gate)
        {
            mode = _session?.Mode ?? JoinMode.InWorld;
            target = CurrentTargetLocked();
            name = JoinText.ServerLabel(ServerNameLocked(), _host);
        }

        string key = target.Address.Length > 0 ? target.Endpoint : ServerKey;
        string v = kept.PickVersion;
        var r = svc.Swap(row.Id, v, key.Length > 0 ? key : "server", name);
        if (!r.Ok)
        {
            Plugin.Logger?.LogWarning("JoinFlow: versions: Use kept " + row.Id + " " + v + " refused: " + r.Message);
            return JoinKeptText.Refused(row.Name, v, r.Message);
        }

        Plugin.Logger?.LogInfo("JoinFlow: versions: Use kept " + row.Id + " " + v + " (" + r.Route + ")");
        bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
        if (r.Route == "now")
        {
            if (mode == JoinMode.InstallWindow)
            {
                // Inside the server's install window: answer the handshake again once the kept copy is declared.
                int gen = ModVersionJoin.ConnectionGeneration;
                ModVersionJoin.WaitThen(new List<(string Id, string Version)> { (row.Id, v) },
                    ModVersionJoin.Clock() + ModVersionJoin.SwapWaitSeconds, gen, () =>
                    {
                        bool sent = ReAnswerHandshake();
                        Plugin.Logger?.LogInfo("JoinFlow: versions: re-answered the handshake after the kept " + row.Id + " " + v + " loaded; sent=" + (sent ? "true" : "false"));
                    });
                return JoinKeptText.SwappedWindow(row.Name, v);
            }

            if (mode == JoinMode.Refused && InWorldNow())
            {
                // INPLACE1: still in this server's world: once the kept copy is declared, answer again here; no rejoin.
                JoinResultOffer inPlace;
                lock (Gate) inPlace = InPlaceOfferLocked(row.Name + " " + v, "in use", "in use");
                int gen = ModVersionJoin.ConnectionGeneration;
                ModVersionJoin.WaitThen(new List<(string Id, string Version)> { (row.Id, v) },
                    ModVersionJoin.Clock() + ModVersionJoin.SwapWaitSeconds, gen, () =>
                    {
                        FinishInPlace(inPlace, "using the kept " + row.Id + " " + v);
                        ShowInPlace(inPlace);
                        RaiseChanged();
                    });
                return JoinKeptText.SwappedInPlace(row.Name, v);
            }

            if (mode == JoinMode.Refused)
            {
                var ready = new JoinResultOffer
                {
                    Kind = canRejoin ? JoinAfterInstall.JoinNow : JoinAfterInstall.Nothing,
                    CanRejoin = canRejoin,
                    Hosted = target.HostedByThisClient,
                    Summary = JoinKeptText.SwappedRefused(row.Name, v) + (canRejoin ? "" : " Join the server again from the server list."),
                };
                if (!r.AwaitingPlace)
                {
                    offer = ready;
                    return offer.Summary;
                }

                // Still being written (the previous copy unloads first): Join now is offered once it is in place, so a
                // join pressed meanwhile does not meet a swap in progress.
                ModVersionJoin.WaitPlaced(svc, ModVersionJoin.Clock() + ModVersionJoin.PlaceForceSeconds + 2,
                    ModVersionJoin.Clock() + ModVersionJoin.PlaceGiveUpSeconds, (ModVersionResult done) =>
                    {
                        bool placed = ModVersionJoin.Placed(done);
                        lock (Gate)
                        {
                            if (_session == null || _session.Closed || _session.Mode != JoinMode.Refused) return;
                            if (placed) _session.ShowResult(ready);
                            else _session.Note(JoinKeptText.Refused(row.Name, v, "the previous copy did not unload"));
                        }

                        RaiseChanged();
                    });
                return JoinKeptText.Swapping(row.Name, v);
            }

            return r.AwaitingPlace ? JoinKeptText.Swapping(row.Name, v) : JoinKeptText.Swapped(row.Name, v);
        }

        offer = new JoinResultOffer
        {
            Kind = JoinAfterInstall.RestartNeeded,
            CanRejoin = canRejoin,
            Hosted = target.HostedByThisClient,
            Summary = JoinKeptText.Queued(row.Name, v),
        };
        if (canRejoin)
        {
            try { offer.ManualRelaunch = Relauncher?.Plan().Mode != RelaunchMode.Automatic; }
            catch { offer.ManualRelaunch = true; }
        }

        return offer.Summary;
    }

    /// <summary>The readback of a row's kept versions, or null.</summary>
    internal static Dictionary<string, object?>? DescribeKept(JoinRequiredRow row)
    {
        var k = row?.Kept;
        if (k == null) return null;
        return new Dictionary<string, object?>
        {
            ["pick"] = k.PickVersion,
            ["pickRoute"] = k.PickRoute,
            ["pickReason"] = k.PickReason,
            ["canKeep"] = k.CanKeepActive,
            ["keepNow"] = k.KeepNow,
            ["keepReason"] = k.KeepReason,
            ["kept"] = k.Kept,
            ["preferred"] = k.Preferred,
            ["activeForServer"] = k.ActiveForServer,
            ["keepLine"] = JoinText.KeepLine(row!),
            ["keepReasonLine"] = JoinText.KeepReasonLine(row!),
            ["forThisServerLine"] = JoinText.ForThisServerLine(row!),
        };
    }
}

/// <summary>JOINFLOW4: the screens' sentences for kept copies. US English.</summary>
internal static class JoinKeptText
{
    public static string Refused(string name, string version, string why) => "Can't use the kept " + name + " " + version + ": " + why;

    public static string Swapped(string name, string version) => name + ": the kept " + version + " is swapped in.";

    public static string Swapping(string name, string version) =>
        name + ": the kept " + version + " is being swapped in (the previous copy unloads first)…";

    public static string SwappedWindow(string name, string version) =>
        name + ": the kept " + version + " is swapped in; the join continues by itself once it loads.";

    public static string SwappedInPlace(string name, string version) =>
        name + ": the kept " + version + " is swapped in; the server is told once it loads. You stay where you are.";

    public static string SwappedRefused(string name, string version) =>
        name + ": the kept " + version + " is swapped in. Join now; no download or restart needed.";

    public static string Queued(string name, string version) =>
        name + ": the kept " + version + " loads at the next start of V Rising.";
}
