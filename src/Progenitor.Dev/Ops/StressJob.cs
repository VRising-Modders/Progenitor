using System;
using System.Collections.Generic;

namespace Progenitor.Dev.Ops;

// Game-free runner behind stress_populate / stress_clear: a list of small steps run a few at a
// time per frame under a millisecond budget, with dependencies (no heart, no floors), per-plot and
// per-owner results, and a status snapshot. The world side is IStressBackend: StressOps supplies
// the live one, the tests a fake.

internal enum StressStepKind
{
    Discover,
    Plan,
    Owner,
    Clan,
    Heart,
    Fuel,
    Floor,
    Station,
    Chest,
    Fill,
    ClearScan,
    ClearUser,
}

/// <summary>What one step did.</summary>
internal sealed class StressOutcome
{
    private StressOutcome(bool ok, string code, string detail)
    {
        Ok = ok;
        Code = code;
        Detail = detail;
    }

    public bool Ok { get; }
    /// <summary>Empty when <see cref="Ok"/>; else a short code such as <c>unproven</c> or <c>spawn_failed</c>.</summary>
    public string Code { get; }
    public string Detail { get; }

    public static StressOutcome Done(string detail = "") => new(true, "", detail ?? "");

    public static StressOutcome Refuse(string code, string detail) => new(false, code ?? "refused", detail ?? "");
}

/// <summary>A stress owner found in the world by <c>stress_clear</c>.</summary>
internal sealed class StressClearTarget
{
    public string Entity { get; set; } = "";
    public string Name { get; set; } = "";
    public ulong PlatformId { get; set; }
    public int Ordinal { get; set; }
    /// <summary>Hearts this user owns; a user who owns one is not removed (no proven heart removal).</summary>
    public int HeartsOwned { get; set; }
    /// <summary>The user's clan entity, empty when none.</summary>
    public string Clan { get; set; } = "";
    public bool HasCharacter { get; set; }
}

/// <summary>The world side of a stress job. Every call runs on the main thread, one step at a time.</summary>
internal interface IStressBackend
{
    /// <summary>False with a reason when this build has no proven path for <paramref name="kind"/>.</summary>
    bool Proven(StressStepKind kind, out string missing);

    /// <summary>Start territory discovery; returns how many territories will be read.</summary>
    int BeginDiscovery();

    /// <summary>Read the next territory; false when there are none left. A null row is an unreadable one.</summary>
    bool DiscoverNext(out StressTerritory? territory);

    /// <summary>Stress users of other seeds and of this seed already in the world (refuse populate over them).</summary>
    void CountExisting(int seed, out int sameSeed, out int otherSeeds);

    /// <summary>
    /// Reserve a <c>User.Index</c> for every owner (<see cref="StressOwnerPlan.UserIndex"/>) before any write,
    /// against every User in the world (<see cref="StressIndexPlan"/>). A refusal ends the job with nothing written.
    /// </summary>
    StressOutcome ReserveOwnerIndexes(IReadOnlyList<StressOwnerPlan> owners);

    StressOutcome CreateOwner(StressOwnerPlan owner);
    StressOutcome CreateClan(StressClanPlan clan, StressPlan plan);
    StressOutcome PlaceHeart(StressPlotPlan plot, StressOwnerPlan owner);
    StressOutcome FuelHeart(StressPlotPlan plot);
    StressOutcome PlaceFloor(StressPlotPlan plot, StressCell cell);
    StressOutcome PlaceStation(StressPlotPlan plot, StressStationPlan station);
    StressOutcome PlaceChest(StressPlotPlan plot, StressChestPlan chest);
    StressOutcome FillChest(StressPlotPlan plot, StressChestPlan chest);

    List<StressClearTarget> FindSeedUsers(int seed);
    StressOutcome RemoveUser(StressClearTarget target);
}

/// <summary>Per-plot progress and result.</summary>
internal sealed class StressPlotResult
{
    public int Territory { get; set; }
    public string Owner { get; set; } = "";
    /// <summary>planned, refused, running, done, partial, skipped.</summary>
    public string Status { get; set; } = "planned";
    public string Reason { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Heart { get; set; }
    public bool Fuelled { get; set; }
    public int Floors { get; set; }
    public int FloorsPlanned { get; set; }
    public int Stations { get; set; }
    public int StationsPlanned { get; set; }
    public int Chests { get; set; }
    public int ChestsPlanned { get; set; }
    public int Filled { get; set; }
    public int Failures { get; set; }
    /// <summary>The first few step refusals, as "kind: code detail".</summary>
    public List<string> Refusals { get; } = new();
    internal bool Blocked;
    internal readonly HashSet<int> PlacedChests = new();
}

/// <summary>Per-owner (populate) or per-user (clear) result.</summary>
internal sealed class StressUserResult
{
    public string Name { get; set; } = "";
    public string PlatformId { get; set; } = "";
    /// <summary>created, removed, refused, skipped.</summary>
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>One populate or clear run.</summary>
internal sealed class StressJob
{
    public const int MaxRefusalsPerPlot = 4;

    private readonly IStressBackend _backend;
    private readonly List<StressStep> _steps = new();
    private readonly List<StressTerritory> _territories = new();
    private readonly Dictionary<int, StressPlotResult> _plots = new();
    private readonly Dictionary<int, StressUserResult> _owners = new();
    private readonly Dictionary<int, StressUserResult> _clans = new();
    private readonly List<StressUserResult> _cleared = new();
    private readonly Dictionary<string, string> _missing = new(StringComparer.Ordinal);
    private int _next;
    private int _discoverTotal;
    private int _unreadable;

    private StressJob(string kind, StressArgs args, IStressBackend backend)
    {
        Kind = kind;
        Args = args;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public string Kind { get; }
    public StressArgs Args { get; }
    /// <summary>discover, plan, execute, done, refused, failed, cancelled.</summary>
    public string Phase { get; private set; } = "discover";
    public string Reason { get; private set; } = "";
    public StressPlan? Plan { get; private set; }
    public int Ticks { get; private set; }
    public int StepsRun { get; private set; }
    public double MaxTickMs { get; private set; }
    public double TotalMs { get; private set; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; private set; }

    public bool Finished => Phase is "done" or "refused" or "failed" or "cancelled";
    /// <summary>True once the plan exists (or the job ended before it): the populate reply can go.</summary>
    public bool Planned => Plan != null || Finished;
    public int StepsTotal => _steps.Count;
    public int StepsDone => _next;
    public IReadOnlyDictionary<string, string> Missing => _missing;
    public IReadOnlyCollection<StressPlotResult> PlotResults => _plots.Values;
    public IReadOnlyCollection<StressUserResult> OwnerResults => _owners.Values;
    public IReadOnlyCollection<StressUserResult> ClanResults => _clans.Values;
    public IReadOnlyList<StressUserResult> Cleared => _cleared;
    public int TerritoriesRead => _territories.Count;
    public int TerritoriesUnreadable => _unreadable;

    public static StressJob Populate(StressArgs args, IStressBackend backend)
    {
        var job = new StressJob("populate", args, backend);
        job._discoverTotal = backend.BeginDiscovery();
        job._steps.Add(new StressStep(StressStepKind.Discover));
        return job;
    }

    public static StressJob Clear(StressArgs args, IStressBackend backend)
    {
        var job = new StressJob("clear", args, backend) { Phase = "execute" };
        job._steps.Add(new StressStep(StressStepKind.ClearScan));
        return job;
    }

    public void Cancel(string reason)
    {
        if (Finished) return;
        Phase = "cancelled";
        Reason = reason ?? "";
        FinishedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Run steps until <see cref="StressArgs.BudgetMs"/> is spent (at least one). Returns true
    /// while work remains. <paramref name="nowMs"/> is a monotonic clock in milliseconds.
    /// </summary>
    public bool Tick(Func<double> nowMs)
    {
        if (Finished) return false;
        double start = nowMs();
        Ticks++;
        try
        {
            do
            {
                if (_next >= _steps.Count)
                {
                    Finish("done", "");
                    break;
                }
                var step = _steps[_next];
                if (step.Kind == StressStepKind.Discover)
                {
                    // Discovery stays on one step until the backend has read every territory.
                    if (_backend.DiscoverNext(out var t))
                    {
                        if (t == null) _unreadable++;
                        else _territories.Add(t);
                        StepsRun++;
                        continue;
                    }
                    _next++;
                    _steps.Insert(_next, new StressStep(StressStepKind.Plan));
                    continue;
                }
                _next++;
                StepsRun++;
                Run(step);
            }
            while (!Finished && nowMs() - start < Args.BudgetMs);
        }
        catch (Exception e)
        {
            Finish("failed", e.GetType().Name + ": " + e.Message);
        }
        double spent = nowMs() - start;
        TotalMs += spent;
        if (spent > MaxTickMs) MaxTickMs = spent;
        if (!Finished && _next >= _steps.Count) Finish("done", "");
        return !Finished;
    }

    private void Finish(string phase, string reason)
    {
        if (Finished) return;
        Phase = phase;
        Reason = reason;
        FinishedUtc = DateTime.UtcNow;
    }

    private void Run(StressStep step)
    {
        switch (step.Kind)
        {
            case StressStepKind.Plan: RunPlan(); return;
            case StressStepKind.Owner: RunOwner(step.Owner!); return;
            case StressStepKind.Clan: RunClan(step.Clan!); return;
            case StressStepKind.ClearScan: RunClearScan(); return;
            case StressStepKind.ClearUser: RunClearUser(step.Target!); return;
            default: RunPlotStep(step); return;
        }
    }

    // ---- populate ---------------------------------------------------------------------------

    private void RunPlan()
    {
        Phase = "plan";
        var plan = StressPlanner.Plan(Args, _territories);
        Plan = plan;
        foreach (var r in plan.Refusals)
            _plots[r.Territory] = new StressPlotResult { Territory = r.Territory, Status = "refused", Reason = r.Reason, Detail = r.Detail };
        foreach (var p in plan.Plots)
        {
            _plots[p.Territory] = new StressPlotResult
            {
                Territory = p.Territory,
                Owner = plan.Owner(p.Owner)?.Name ?? "",
                FloorsPlanned = p.Floors.Count,
                StationsPlanned = p.Stations.Count,
                ChestsPlanned = p.Chests.Count,
            };
        }
        foreach (var o in plan.Owners)
            _owners[o.Ordinal] = new StressUserResult { Name = o.Name, PlatformId = o.PlatformId.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = "planned" };
        foreach (var c in plan.Clans)
            _clans[c.Ordinal] = new StressUserResult { Name = c.Name, Status = "planned" };

        foreach (StressStepKind kind in new[] { StressStepKind.Owner, StressStepKind.Clan, StressStepKind.Heart, StressStepKind.Fuel, StressStepKind.Floor, StressStepKind.Station, StressStepKind.Chest, StressStepKind.Fill })
        {
            if (!_backend.Proven(kind, out var why)) _missing[KindName(kind)] = why;
        }

        if (plan.Plots.Count == 0)
        {
            Finish("refused", "no usable plot (see results)");
            return;
        }
        if (Args.DryRun)
        {
            Finish("done", "dry run: planned only, nothing written");
            return;
        }
        _backend.CountExisting(Args.Seed, out int same, out int other);
        if (same > 0)
        {
            Finish("refused", $"seed {Args.Seed} already has {same} stress owner(s) in this world; run stress_clear first");
            return;
        }
        if (other > 0)
        {
            Finish("refused", $"{other} stress owner(s) of another seed are in this world (their names would clash); clear that seed first");
            return;
        }
        if (_missing.Count > 0 && !Args.Partial)
        {
            Finish("refused", "unproven: this build has no proven path for " + string.Join(", ", _missing.Keys) + "; nothing was written (partial:true runs only the proven steps)");
            return;
        }

        if (plan.Owners.Count > 0 && !_missing.ContainsKey(KindName(StressStepKind.Owner)))
        {
            var reserved = _backend.ReserveOwnerIndexes(plan.Owners);
            if (!reserved.Ok)
            {
                Finish("refused", "owner_index: " + reserved.Detail + "; nothing was written");
                return;
            }
        }

        Phase = "execute";
        foreach (var o in plan.Owners) _steps.Add(new StressStep(StressStepKind.Owner) { Owner = o });
        foreach (var c in plan.Clans) _steps.Add(new StressStep(StressStepKind.Clan) { Clan = c });
        foreach (var p in plan.Plots)
        {
            _steps.Add(new StressStep(StressStepKind.Heart) { Plot = p });
            _steps.Add(new StressStep(StressStepKind.Fuel) { Plot = p });
            foreach (var cell in p.Floors) _steps.Add(new StressStep(StressStepKind.Floor) { Plot = p, Cell = cell });
            foreach (var s in p.Stations) _steps.Add(new StressStep(StressStepKind.Station) { Plot = p, Station = s });
            foreach (var c in p.Chests) _steps.Add(new StressStep(StressStepKind.Chest) { Plot = p, Chest = c });
            foreach (var c in p.Chests) _steps.Add(new StressStep(StressStepKind.Fill) { Plot = p, Chest = c });
        }
    }

    private void RunOwner(StressOwnerPlan owner)
    {
        var row = _owners[owner.Ordinal];
        var outcome = Proven(StressStepKind.Owner) ?? Safe(() => _backend.CreateOwner(owner));
        row.Status = outcome.Ok ? "created" : "refused";
        row.Reason = outcome.Code;
        row.Detail = outcome.Detail;
    }

    private void RunClan(StressClanPlan clan)
    {
        var row = _clans[clan.Ordinal];
        foreach (int m in clan.Members)
        {
            if (_owners.TryGetValue(m, out var o) && o.Status == "created") continue;
            row.Status = "skipped";
            row.Reason = "owner_failed";
            row.Detail = StressIdentity.OwnerName(m) + " was not created";
            return;
        }
        var outcome = Proven(StressStepKind.Clan) ?? Safe(() => _backend.CreateClan(clan, Plan!));
        row.Status = outcome.Ok ? "created" : "refused";
        row.Reason = outcome.Code;
        row.Detail = outcome.Detail;
    }

    private void RunPlotStep(StressStep step)
    {
        var plot = step.Plot!;
        var row = _plots[plot.Territory];
        if (row.Blocked) return;
        if (row.Status == "planned") row.Status = "running";

        var owner = Plan!.Owner(plot.Owner)!;
        if (step.Kind == StressStepKind.Heart && _owners[owner.Ordinal].Status != "created")
        {
            Block(row, "owner_failed", owner.Name + " was not created");
            return;
        }

        StressOutcome outcome = Proven(step.Kind) ?? Safe(() => step.Kind switch
        {
            StressStepKind.Heart => _backend.PlaceHeart(plot, owner),
            StressStepKind.Fuel => _backend.FuelHeart(plot),
            StressStepKind.Floor => _backend.PlaceFloor(plot, step.Cell),
            StressStepKind.Station => _backend.PlaceStation(plot, step.Station!),
            StressStepKind.Chest => _backend.PlaceChest(plot, step.Chest!),
            StressStepKind.Fill => row.PlacedChests.Contains(step.Chest!.Number)
                ? _backend.FillChest(plot, step.Chest!)
                : StressOutcome.Refuse("no_chest", "chest " + step.Chest!.Number + " was not placed"),
            _ => StressOutcome.Refuse("unknown_step", step.Kind.ToString()),
        });

        if (outcome.Ok)
        {
            switch (step.Kind)
            {
                case StressStepKind.Heart: row.Heart = true; break;
                case StressStepKind.Fuel: row.Fuelled = true; break;
                case StressStepKind.Floor: row.Floors++; break;
                case StressStepKind.Station: row.Stations++; break;
                case StressStepKind.Chest: row.Chests++; row.PlacedChests.Add(step.Chest!.Number); break;
                case StressStepKind.Fill: row.Filled++; break;
            }
        }
        else if (step.Kind == StressStepKind.Heart)
        {
            // Nothing else can join a castle without its heart.
            Block(row, outcome.Code, "heart: " + outcome.Detail);
            return;
        }
        else
        {
            row.Failures++;
            if (row.Refusals.Count < MaxRefusalsPerPlot)
                row.Refusals.Add(KindName(step.Kind) + ": " + outcome.Code + (outcome.Detail.Length > 0 ? " " + outcome.Detail : ""));
        }

        if (IsLastStepOfPlot(step))
            row.Status = row.Failures == 0 ? "done" : "partial";
    }

    private bool IsLastStepOfPlot(StressStep step) =>
        _next >= _steps.Count || !ReferenceEquals(_steps[_next].Plot, step.Plot);

    private static void Block(StressPlotResult row, string code, string detail)
    {
        row.Blocked = true;
        row.Status = "refused";
        row.Reason = code;
        row.Detail = detail;
    }

    /// <summary>A backend step that throws is that step's refusal, not the end of the job.</summary>
    private static StressOutcome Safe(Func<StressOutcome> step)
    {
        try { return step() ?? StressOutcome.Refuse("no_outcome", "the step returned nothing"); }
        catch (Exception e) { return StressOutcome.Refuse("exception", e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>A refusal when this build has no proven path for the step, else null.</summary>
    private StressOutcome? Proven(StressStepKind kind) =>
        _missing.TryGetValue(KindName(kind), out var why) ? StressOutcome.Refuse("unproven", why) : null;

    // ---- clear ------------------------------------------------------------------------------

    private void RunClearScan()
    {
        var targets = _backend.FindSeedUsers(Args.Seed);
        targets.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        foreach (var t in targets)
        {
            if (Args.DryRun)
            {
                _cleared.Add(new StressUserResult { Name = t.Name, PlatformId = Pid(t), Status = "planned", Detail = "dry run" });
                continue;
            }
            _steps.Add(new StressStep(StressStepKind.ClearUser) { Target = t });
        }
    }

    private void RunClearUser(StressClearTarget t)
    {
        var row = new StressUserResult { Name = t.Name, PlatformId = Pid(t) };
        _cleared.Add(row);
        if (t.HeartsOwned > 0)
        {
            row.Status = "refused";
            row.Reason = "heart_left";
            row.Detail = $"owns {t.HeartsOwned} castle heart(s); removing a heart has no proven path, so the owner stays";
            return;
        }
        if (t.Clan.Length > 0)
        {
            row.Status = "refused";
            row.Reason = "clan_member";
            row.Detail = "member of clan " + t.Clan + "; removing a clan member has no proven path, so the owner stays";
            return;
        }
        var outcome = Safe(() => _backend.RemoveUser(t));
        row.Status = outcome.Ok ? "removed" : "refused";
        row.Reason = outcome.Code;
        row.Detail = outcome.Detail;
    }

    private static string Pid(StressClearTarget t) => t.PlatformId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string KindName(StressStepKind kind) => kind switch
    {
        StressStepKind.Owner => "owner",
        StressStepKind.Clan => "clan",
        StressStepKind.Heart => "heart",
        StressStepKind.Fuel => "fuel",
        StressStepKind.Floor => "floor",
        StressStepKind.Station => "station",
        StressStepKind.Chest => "chest",
        StressStepKind.Fill => "fill",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ---- status -----------------------------------------------------------------------------

    /// <summary>The status reply. <paramref name="plot"/> adds that plot's full layout.</summary>
    public Dictionary<string, object?> Describe(int? plot = null, bool rows = true)
    {
        int ownersCreated = 0, ownersRefused = 0;
        foreach (var o in _owners.Values)
        {
            if (o.Status == "created") ownersCreated++;
            else if (o.Status is "refused" or "skipped") ownersRefused++;
        }
        var byStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _plots.Values)
            byStatus[r.Status] = byStatus.TryGetValue(r.Status, out int n) ? n + 1 : 1;
        var byReason = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _plots.Values)
            if (r.Reason.Length > 0) byReason[r.Reason] = byReason.TryGetValue(r.Reason, out int n) ? n + 1 : 1;

        var reply = new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["seed"] = Args.Seed,
            ["phase"] = Phase,
            ["running"] = !Finished,
            ["reason"] = Reason,
            ["startedUtc"] = StartedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["finishedUtc"] = FinishedUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["budgetMs"] = Args.BudgetMs,
            ["ticks"] = Ticks,
            ["maxTickMs"] = Math.Round(MaxTickMs, 2),
            ["totalMs"] = Math.Round(TotalMs, 1),
            ["steps"] = new Dictionary<string, object?> { ["done"] = StepsDone, ["total"] = StepsTotal, ["run"] = StepsRun },
            ["dryRun"] = Args.DryRun,
            ["partial"] = Args.Partial,
            ["missing"] = new SortedDictionary<string, string>(_missing, StringComparer.Ordinal),
        };

        if (Kind == "clear")
        {
            int removed = 0, kept = 0;
            foreach (var c in _cleared) { if (c.Status == "removed") removed++; else if (c.Status == "refused") kept++; }
            reply["removed"] = removed;
            reply["kept"] = kept;
            reply["users"] = rows ? UserRows(_cleared) : null;
            return reply;
        }

        reply["territories"] = new Dictionary<string, object?> { ["expected"] = _discoverTotal, ["read"] = _territories.Count, ["unreadable"] = _unreadable };
        if (Plan != null)
        {
            reply["notes"] = Plan.Notes;
            reply["owners"] = new Dictionary<string, object?> { ["planned"] = Plan.Owners.Count, ["created"] = ownersCreated, ["refused"] = ownersRefused };
            reply["clans"] = Plan.Clans.Count;
            reply["plots"] = new Dictionary<string, object?> { ["planned"] = Plan.Plots.Count, ["refusedAtPlan"] = Plan.Refusals.Count, ["byStatus"] = byStatus, ["byReason"] = byReason };
        }
        if (rows)
        {
            var plotRows = new List<object>();
            var ordered = new List<StressPlotResult>(_plots.Values);
            ordered.Sort((a, b) => a.Territory.CompareTo(b.Territory));
            foreach (var r in ordered) plotRows.Add(PlotRow(r));
            reply["results"] = plotRows;
            var ownerRows = new List<StressUserResult>(_owners.Values);
            reply["ownerResults"] = UserRows(ownerRows);
            reply["clanResults"] = Plan == null ? null : ClanRows();
        }
        if (plot != null && Plan != null)
            reply["plot"] = PlotDetail(plot.Value);
        return reply;
    }

    private static List<object> UserRows(IEnumerable<StressUserResult> rows)
    {
        var list = new List<object>();
        foreach (var r in rows)
            list.Add(new Dictionary<string, object?> { ["name"] = r.Name, ["platformId"] = r.PlatformId, ["status"] = r.Status, ["reason"] = r.Reason, ["detail"] = r.Detail });
        return list;
    }

    private List<object> ClanRows()
    {
        var list = new List<object>();
        foreach (var c in Plan!.Clans)
        {
            var r = _clans[c.Ordinal];
            var names = new List<string>();
            foreach (int m in c.Members) names.Add(StressIdentity.OwnerName(m));
            list.Add(new Dictionary<string, object?> { ["name"] = c.Name, ["members"] = names, ["status"] = r.Status, ["reason"] = r.Reason, ["detail"] = r.Detail });
        }
        return list;
    }

    private static Dictionary<string, object?> PlotRow(StressPlotResult r) => new()
    {
        ["plot"] = r.Territory,
        ["owner"] = r.Owner,
        ["status"] = r.Status,
        ["reason"] = r.Reason,
        ["detail"] = r.Detail,
        ["heart"] = r.Heart,
        ["fuel"] = r.Fuelled,
        ["floors"] = r.Floors + "/" + r.FloorsPlanned,
        ["stations"] = r.Stations + "/" + r.StationsPlanned,
        ["chests"] = r.Chests + "/" + r.ChestsPlanned,
        ["filled"] = r.Filled,
        ["refusals"] = r.Refusals.Count == 0 ? null : r.Refusals,
    };

    private object? PlotDetail(int territory)
    {
        StressPlotPlan? p = null;
        foreach (var x in Plan!.Plots) if (x.Territory == territory) { p = x; break; }
        if (p == null)
        {
            foreach (var r in Plan.Refusals)
                if (r.Territory == territory) return new Dictionary<string, object?> { ["plot"] = territory, ["refused"] = r.Reason, ["detail"] = r.Detail };
            return new Dictionary<string, object?> { ["plot"] = territory, ["refused"] = "not_in_plan" };
        }
        var owner = Plan.Owner(p.Owner);
        var floors = new List<object>();
        foreach (var c in p.Floors) floors.Add(new[] { c.X, c.Z });
        var stations = new List<object>();
        foreach (var s in p.Stations)
            stations.Add(new Dictionary<string, object?> { ["n"] = s.Number, ["kind"] = s.Kind, ["prefabs"] = s.Prefabs, ["x"] = s.Cell.X, ["z"] = s.Cell.Z });
        var chests = new List<object>();
        foreach (var c in p.Chests)
            chests.Add(new Dictionary<string, object?> { ["n"] = c.Number, ["kind"] = c.Kind, ["prefabs"] = c.Prefabs, ["x"] = c.X, ["z"] = c.Z, ["slotFraction"] = c.SlotFraction, ["nearlyFull"] = c.NearlyFull, ["items"] = c.Items });
        return new Dictionary<string, object?>
        {
            ["plot"] = p.Territory,
            ["owner"] = owner?.Name,
            ["clan"] = owner == null || owner.Clan == 0 ? null : StressIdentity.ClanName(owner.Clan),
            ["heart"] = new Dictionary<string, object?> { ["prefab"] = StressLayout.HeartPrefab, ["x"] = p.HeartX, ["z"] = p.HeartZ, ["tileX"] = p.PivotX, ["tileY"] = p.PivotY, ["fuel"] = p.Fuel, ["fuelItem"] = StressLayout.FuelItem },
            ["quadrant"] = p.Quadrant,
            ["floorPrefab"] = StressLayout.FloorPrefab,
            ["floors"] = floors,
            ["stations"] = stations,
            ["chests"] = chests,
        };
    }

    private sealed class StressStep
    {
        public StressStep(StressStepKind kind) => Kind = kind;
        public StressStepKind Kind { get; }
        public StressOwnerPlan? Owner { get; set; }
        public StressClanPlan? Clan { get; set; }
        public StressPlotPlan? Plot { get; set; }
        public StressCell Cell { get; set; }
        public StressStationPlan? Station { get; set; }
        public StressChestPlan? Chest { get; set; }
        public StressClearTarget? Target { get; set; }
    }
}
