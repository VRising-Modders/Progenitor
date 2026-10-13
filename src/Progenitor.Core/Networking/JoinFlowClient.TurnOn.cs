using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>JOINTURNON: what the Mods list's switch did for the join screens (or the bridge).</summary>
internal sealed class JoinSwitchResult
{
    public bool Ok { get; init; }

    /// <summary>The switch's own sentence (<c>Turned on.</c>, <c>Turns on at the next start.</c>, a refusal).</summary>
    public string Message { get; init; } = "";

    /// <summary>The switch is live: the change happened now (else it is recorded for the next start).</summary>
    public bool Now { get; init; }

    /// <summary>The switch's mode: <c>live</c>, <c>restart</c>, <c>unavailable</c>, <c>refused</c>, <c>hidden</c>.</summary>
    public string Mode { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Live 2026-10-05: turned on, but the per-server pause keeps it unloaded until the player leaves.</summary>
    public bool PausedHere { get; init; }

    /// <summary>Turned on, but it loads only when the player leaves (the joined server holds other mods paused).</summary>
    public bool LoadsAfterLeave { get; init; }

    public static JoinSwitchResult Fail(string message) => new() { Ok = false, Message = message ?? "" };
}

/// <summary>
/// JOINTURNON: the join screens' Turn on. A mod the player turned off in the Mods list is installed but not loaded, so
/// the handshake does not see it and the page used to offer a needless download. The screens now turn it on through the
/// Mods list's own switch (<c>ModSwitch.Set</c>, the same enable state, so the list shows it on afterwards): a
/// hot-loadable Progenitor mod loads now (the join continues as for other live fixes), anything else is recorded for
/// the next start and feeds Restart and rejoin.
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>How long a turned-on hot mod may take to be declared before the screens move on anyway.</summary>
    internal const double TurnOnWaitSeconds = 8;

    /// <summary>
    /// The player's turned-off copy of a mod as the Mods list's switch sees it, or null (live: <c>ModsMenu.JoinOffCopyOf</c>,
    /// set by <see cref="JoinFlowLive"/>; tests pin it). Null probe: no row offers Turn on.
    /// </summary>
    internal static Func<string, JoinOffCopy?>? OffCopyProbe { get; set; }

    /// <summary>Press the Mods list's switch for a mod (id, on) (live: <c>ModsMenu.SwitchForJoin</c>; tests pin it).</summary>
    internal static Func<string, bool, JoinSwitchResult>? SwitchProbe { get; set; }

    // Mods the screens turned on during this join: id → (name, version, route now|restart, required). The rejoin record and
    // the batch's result read it; a new join attempt forgets it.
    private static readonly Dictionary<string, (string Name, string Version, string Route, bool Required)> TurnedOnThisJoin =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class TurnOnWait
    {
        public List<(string Id, string Version)> WaitFor { get; init; } = new();
        public double Deadline { get; init; }
        public JoinMode Mode { get; init; }
        public int Generation { get; init; }
        public string Names { get; init; } = "";
    }

    private static readonly List<TurnOnWait> TurnOnWaits = new();

    // Called with Gate held from OnJoinAttempt.
    private static void ForgetTurnOnsLocked()
    {
        TurnedOnThisJoin.Clear();
        TurnOnWaits.Clear();
    }

    /// <summary>
    /// The facts' off-copy lookup for one model build: the probe asked once per mod (it reads the switch's state file
    /// and the preloader's renamed files), or null without a probe.
    /// </summary>
    private static Func<string, JoinOffCopy?>? OffCopyOfForBuild()
    {
        var probe = OffCopyProbe;
        if (probe == null) return null;
        var cache = new Dictionary<string, JoinOffCopy?>(StringComparer.OrdinalIgnoreCase);
        return id =>
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (cache.TryGetValue(id, out var hit)) return hit;
            JoinOffCopy? off;
            try { off = probe(id); }
            catch { off = null; }
            cache[id] = off;
            return off;
        };
    }

    private static JoinOffCopy? SafeOffCopy(string id)
    {
        try { return OffCopyProbe?.Invoke(id); }
        catch { return null; }
    }

    private static JoinSwitchResult SafeSwitch(string id, bool on)
    {
        var probe = SwitchProbe;
        if (probe == null) return JoinSwitchResult.Fail("the Mods list's switch is not available here");
        try { return probe(id, on) ?? JoinSwitchResult.Fail("the switch did not answer"); }
        catch (Exception e) { return JoinSwitchResult.Fail(e.Message); }
    }

    /// <summary>The effects' Turn on: each target through the Mods list's switch, then what the join needs next.</summary>
    private static string TurnOnLive(IReadOnlyList<JoinTurnOnTarget> targets, out JoinResultOffer? offer)
    {
        offer = null;
        if (SwitchProbe == null || targets == null || targets.Count == 0) return JoinText.TurnOnNotAvailable;
        JoinMode mode;
        lock (Gate) mode = _session?.Mode ?? (_refused ? JoinMode.Refused : _window ? JoinMode.InstallWindow : JoinMode.InWorld);

        var parts = new List<string>();
        var waitFor = new List<(string Id, string Version)>();
        var nowNames = new List<string>();
        var nowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool restart = false;
        foreach (var t in targets)
        {
            if (t == null || t.Id.Length == 0) continue;
            if (t.TurnsOnAtNextStart)
            {
                // Already switched on (an earlier press, or the Mods list): nothing to switch, the restart does it.
                restart = true;
                parts.Add(JoinText.TurnsOnAtNextStart(t.Name));
                NoteTurnedOn(t, "restart");
                continue;
            }

            var r = SafeSwitch(t.Id, on: true);
            Plugin.Logger?.LogInfo("JoinFlow: turn on " + t.Id + " " + t.Version + " -> " + (r.Ok ? (r.Now ? "now" : "restart") : "refused")
                + (r.Message.Length > 0 ? ": " + r.Message : ""));
            if (!r.Ok)
            {
                parts.Add(JoinText.TurnOnFailed(t.Name, r.Message));
                continue;
            }

            if (r.Now && (r.PausedHere || r.LoadsAfterLeave))
            {
                // Live 2026-10-05: on, but the per-server pause keeps it unloaded here: one accurate sentence, nothing
                // to wait for (never "it loads in a moment").
                parts.Add(r.PausedHere ? JoinText.TurnedOnPausedHere(t.Name) : JoinText.TurnedOnAfterLeave(t.Name));
                NoteTurnedOn(t, "paused");
            }
            else if (r.Now)
            {
                nowNames.Add(t.Name);
                nowIds.Add(t.Id);
                if (t.Version.Length > 0) waitFor.Add((t.Id, t.Version));
                NoteTurnedOn(t, "now");
                // The switch's own caveat (loads once its previous copy is cleaned up) is said too.
                if (r.Message.Length > 0 && !string.Equals(r.Message.Trim(), "Turned on.", StringComparison.Ordinal))
                    parts.Add(t.Name + ": " + r.Message.Trim());
            }
            else
            {
                restart = true;
                parts.Add(JoinText.TurnsOnAtNextStart(t.Name));
                NoteTurnedOn(t, "restart");
            }
        }

        if (nowNames.Count > 0)
        {
            string names = string.Join(", ", nowNames);
            parts.Insert(0, mode == JoinMode.InstallWindow ? JoinText.TurnedOnWindow(names) : JoinText.TurnedOnLoading(names));
            lock (Gate)
                TurnOnWaits.Add(new TurnOnWait
                {
                    WaitFor = waitFor,
                    Deadline = Clock() + TurnOnWaitSeconds,
                    Mode = mode,
                    Generation = _joinGeneration,
                    Names = names,
                });
        }

        // The rows read the switch again: a restart turn-on is pending now, a hot one loads.
        Refresh();
        string summary = string.Join(" ", parts);
        if (restart)
        {
            lock (Gate) offer = RestartOfferAfterTurnOnLocked(summary, nowIds);
        }

        return offer?.Summary ?? summary;
    }

    // Called with Gate held: what the Result page offers after a restart-route turn-on — Restart and rejoin once nothing
    // else on the page still needs attention (the rest needs its own fix first), else null (the line says it).
    private static JoinResultOffer? RestartOfferAfterTurnOnLocked(string summary, ICollection<string> loadingNow)
    {
        var model = _session?.Model ?? JoinPageModel.Build(BuildFactsLocked());
        foreach (var r in model.Required)
        {
            if (r.Ready || r.TurnOnPending || loadingNow.Contains(r.Id)) continue;
            var state = InstallStateOfLocked(r.Id);
            if (state != null && state.Succeeded) continue;
            return null;
        }

        var target = _batchTarget ?? CurrentTargetLocked();
        bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
        var offer = new JoinResultOffer
        {
            Kind = JoinAfterInstall.RestartNeeded,
            CanRejoin = canRejoin,
            Hosted = target.HostedByThisClient,
            Summary = summary,
        };
        if (canRejoin)
        {
            try { offer.ManualRelaunch = Relauncher?.Plan().Mode != RelaunchMode.Automatic; }
            catch { offer.ManualRelaunch = true; }
        }

        return offer;
    }

    // Called with Gate held.
    private static JoinInstallRowState? InstallStateOfLocked(string key) =>
        _batch?.Row(key) ?? (RefusedThisJoin.TryGetValue(key, out var refused) ? refused : null);

    private static void NoteTurnedOn(JoinTurnOnTarget t, string route)
    {
        lock (Gate) TurnedOnThisJoin[t.Id] = (t.Name, t.Version, route, t.Required);
    }

    /// <summary>
    /// JOINTURNON: an install, update or server copy of a mod the player turned off switches it on first (a Progenitor
    /// mod only), so the copy the fix brings is not held off by the old choice and the Mods list shows it on.
    /// </summary>
    private static void TurnOnForFix(IEnumerable<string> ids)
    {
        if (SwitchProbe == null || OffCopyProbe == null) return;
        foreach (string id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            var off = SafeOffCopy(id);
            if (off == null || !off.TurnsOnWithInstall || off.TurnsOnAtNextStart) continue;
            var r = SafeSwitch(id, on: true);
            Plugin.Logger?.LogInfo("JoinFlow: " + id + " was turned off in Mods; the fix turns it on: " + (r.Ok ? "ok" : "refused")
                + (r.Message.Length > 0 ? " (" + r.Message + ")" : ""));
        }
    }

    /// <summary>Once a frame: the hot turn-ons whose plugins are now declared (or whose wait ran out) move the join on.</summary>
    private static void TickTurnOns(double now)
    {
        List<TurnOnWait>? due = null;
        lock (Gate)
        {
            if (TurnOnWaits.Count == 0) return;
            for (int i = TurnOnWaits.Count - 1; i >= 0; i--)
            {
                var w = TurnOnWaits[i];
                if (w.Generation != _joinGeneration)
                {
                    TurnOnWaits.RemoveAt(i);
                    continue;
                }

                bool declared;
                try { declared = ModVersionJoin.AllDeclared(w.WaitFor); }
                catch { declared = false; }
                if (!declared && now < w.Deadline) continue;
                TurnOnWaits.RemoveAt(i);
                (due ??= new List<TurnOnWait>()).Add(w);
            }
        }

        if (due == null) return;
        foreach (var w in due) FinishTurnOn(w);
    }

    private static void FinishTurnOn(TurnOnWait w)
    {
        // This client's answer now carries the turned-on mods: the rows re-evaluate (a refusal's and a window's model use
        // the join's own tables, so they take the fresh answer).
        lock (Gate)
        {
            var provided = ModManifestDeny.Provided();
            if (provided.Count > 0) _provided = Copy(provided);
        }

        Refresh();
        if (w.Mode == JoinMode.InstallWindow)
        {
            bool sent;
            try { sent = ReAnswerHandshake(); }
            catch { sent = false; }
            Plugin.Logger?.LogInfo("JoinFlow: re-answered the handshake after turning on " + w.Names + "; sent=" + (sent ? "true" : "false"));
            return;
        }

        JoinResultOffer? inPlace = null;
        lock (Gate)
        {
            var s = _session;
            bool open = s != null && !s.Closed && !s.IsPreview && s.Mode != JoinMode.Rejoin;
            // INPLACE1: still in this server's world: answered again below, no rejoin, even when the player closed the
            // screens meanwhile (the result is then a notice).
            if (w.Mode == JoinMode.Refused && InWorldNow() && (open ? s!.Model : JoinPageModel.Build(BuildFactsLocked())).AllRequiredReady)
                inPlace = InPlaceOfferLocked(w.Names, "turned on", "turned on and loaded");
            else if (!open) return;
            else if (w.Mode == JoinMode.Refused && s!.Model.AllRequiredReady)
            {
                var target = CurrentTargetLocked();
                bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
                s.ShowResult(new JoinResultOffer
                {
                    Kind = canRejoin ? JoinAfterInstall.JoinNow : JoinAfterInstall.Nothing,
                    CanRejoin = canRejoin,
                    Hosted = target.HostedByThisClient,
                    Summary = JoinText.TurnedOnRefused(w.Names) + (canRejoin ? "" : " Join the server again from the server list."),
                });
            }
            else if (s!.Page == JoinPage.Mods) s.Note(JoinText.TurnedOnNow(w.Names));
        }

        if (inPlace != null)
        {
            FinishInPlace(inPlace, "turning on " + w.Names);
            ShowInPlace(inPlace);
        }

        RaiseChanged();
    }

    /// <summary>
    /// The bridge's <c>join_ui mod_switch</c>: press the Mods list's switch for <paramref name="id"/> without the UI, then
    /// refresh the screens. The reply's <c>switch</c> says what the switch did; <c>offCopy</c> what the screens now see.
    /// </summary>
    internal static Dictionary<string, object?> BridgeSwitch(string id, bool on)
    {
        var r = SafeSwitch(id ?? "", on);
        Refresh();
        var off = SafeOffCopy(id ?? "");
        return new Dictionary<string, object?>
        {
            ["id"] = id ?? "",
            ["on"] = on,
            ["ok"] = r.Ok,
            ["message"] = r.Message,
            ["now"] = r.Now,
            ["mode"] = r.Mode,
            ["name"] = r.Name,
            ["offCopy"] = DescribeOffCopy(off),
        };
    }

    /// <summary>The readback of an off copy, or null.</summary>
    internal static Dictionary<string, object?>? DescribeOffCopy(JoinOffCopy? off) => off == null ? null : new Dictionary<string, object?>
    {
        ["version"] = off.Version,
        ["route"] = off.PausedHere ? "paused" : off.Route,
        ["appliesNow"] = off.AppliesNow && !off.TurnsOnAtNextStart,
        ["turnsOnAtNextStart"] = off.TurnsOnAtNextStart,
        ["turnsOnWithInstall"] = off.TurnsOnWithInstall,
        ["pausedHere"] = off.PausedHere,
    };

    private static List<object?> DescribeTurnedOnLocked()
    {
        var list = new List<object?>();
        foreach (var kv in TurnedOnThisJoin)
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = kv.Key, ["name"] = kv.Value.Name, ["version"] = kv.Value.Version, ["route"] = kv.Value.Route, ["required"] = kv.Value.Required,
            });
        return list;
    }
}
