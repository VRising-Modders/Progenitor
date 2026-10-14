using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: what a <c>ctx.Hud.Notify</c> notice is about. On the game's own announcement banner every kind looks the same
/// (the banner has one style); the framework band that stands in for it when the banner is not there (main menu, no
/// HUD) tints its two rules with the kind's accent.
/// </summary>
public enum UiNoticeKind
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Where a notice is in its life. <see cref="Done"/> and <see cref="Dropped"/> are final.</summary>
public enum UiNoticeState
{
    /// <summary>Waiting its turn: one notice shows at a time, in the order they were posted.</summary>
    Queued = 0,

    /// <summary>On screen now.</summary>
    Showing = 1,

    /// <summary>Was shown and its time is up (or it was disposed while showing).</summary>
    Done = 2,

    /// <summary>
    /// Never shown: disposed while queued, a duplicate of a notice posted moments before, over the rate limit, pushed
    /// out of a full queue, the owner unloaded, the call ran on the server, or nothing could present it.
    /// </summary>
    Dropped = 3,
}

/// <summary>
/// A notice posted with <c>ctx.Hud.Notify</c>. Dispose takes it back: a queued notice is dropped, a showing one ends
/// (the framework band hides at once; the game's banner finishes its own animation, which cannot be cut short).
/// </summary>
public interface IUiNotice : IDisposable
{
    UiNoticeState State { get; }
}

/// <summary>Game-free rules of the notice queue (P7). The Unity half only presents what <see cref="UiNoticeQueue"/> hands it.</summary>
internal static class UiNoticeRules
{
    /// <summary>At most this many notices wait; posting one more drops the oldest waiting one.</summary>
    internal const int MaxQueued = 8;

    /// <summary>The same header and detail posted again within this many seconds is dropped as a duplicate.</summary>
    internal const double DedupeWindowSeconds = 4.0;

    /// <summary>Rate limit per owner: at most <see cref="RateLimitCount"/> notices in <see cref="RateLimitWindowSeconds"/>.</summary>
    internal const int RateLimitCount = 6;

    internal const double RateLimitWindowSeconds = 10.0;

    /// <summary>Pause between one notice ending and the next starting, so two banners never read as one.</summary>
    internal const double GapSeconds = 0.35;

    /// <summary>
    /// How long a notice holds the screen when the caller passes 0 and the presenter has no length of its own (the
    /// framework band). The game's banner reports its own animation length instead.
    /// </summary>
    internal const double DefaultSeconds = 4.0;

    internal const double MinSeconds = 1.0;

    internal const double MaxSeconds = 30.0;

    /// <summary>
    /// The longest the queue waits for the game's own banner (a day/night or achievement announcement) to finish
    /// before showing anyway: a busy flag that never clears must not hold notices forever.
    /// </summary>
    internal const double MaxHostWaitSeconds = 8.0;

    /// <summary>Longest header and detail kept; longer text is cut with an ellipsis.</summary>
    internal const int MaxHeaderChars = 120;

    internal const int MaxDetailChars = 200;

    /// <summary>The caller's seconds, clamped; 0 or less (or NaN) is "the presenter's default" (<paramref name="presenterDefault"/>).</summary>
    internal static double Seconds(double requested, double presenterDefault)
    {
        double fallback = presenterDefault > 0 && !double.IsNaN(presenterDefault) && !double.IsInfinity(presenterDefault)
            ? presenterDefault
            : DefaultSeconds;
        double s = requested > 0 && !double.IsNaN(requested) && !double.IsInfinity(requested) ? requested : fallback;
        return Math.Max(MinSeconds, Math.Min(MaxSeconds, s));
    }

    /// <summary>One line of notice text: trimmed, line breaks folded to spaces, cut to <paramref name="max"/> with an ellipsis.</summary>
    internal static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text!.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (s.Length <= max) return s;
        return s.Substring(0, Math.Max(0, max - 1)).TrimEnd() + "…";
    }

    /// <summary>The dedupe identity: header and detail, ordinal.</summary>
    internal static string DedupeKey(string header, string detail) => header + "\n" + detail;
}

/// <summary>
/// One posted notice: the handle a plugin holds and the queue's record. Thread-safe through the owning queue's lock.
/// </summary>
internal sealed class UiNoticeHandle : IUiNotice
{
    private readonly UiNoticeQueue? _queue;

    internal UiNoticeHandle(UiNoticeQueue? queue, object? owner, string header, string detail, UiNoticeKind kind,
        double seconds, double postedAt, long serial)
    {
        _queue = queue;
        Owner = owner;
        Header = header;
        Detail = detail;
        Kind = kind;
        RequestedSeconds = seconds;
        PostedAt = postedAt;
        Serial = serial;
    }

    /// <summary>A handle that was never queued (server side, empty text): already <see cref="UiNoticeState.Dropped"/>.</summary>
    internal static UiNoticeHandle Inert(string header = "", string detail = "", UiNoticeKind kind = UiNoticeKind.Info)
    {
        var h = new UiNoticeHandle(null, null, header, detail, kind, 0, 0, 0);
        h.StateValue = UiNoticeState.Dropped;
        h.DropReason = "inert";
        return h;
    }

    internal object? Owner { get; }

    internal string Header { get; }

    internal string Detail { get; }

    internal UiNoticeKind Kind { get; }

    /// <summary>What the caller asked for (0 = the presenter's default).</summary>
    internal double RequestedSeconds { get; }

    internal double PostedAt { get; }

    internal long Serial { get; }

    /// <summary>When it went on screen; NaN before.</summary>
    internal double ShownAt { get; set; } = double.NaN;

    /// <summary>When its slot ends; NaN before it shows.</summary>
    internal double EndsAt { get; set; } = double.NaN;

    /// <summary>Why it was dropped (tests, the bridge, a log line); empty otherwise.</summary>
    internal string DropReason { get; set; } = "";

    /// <summary>The presenter's own token for a showing notice (the Unity half's record); null otherwise.</summary>
    internal object? PresenterState { get; set; }

    internal UiNoticeState StateValue { get; set; } = UiNoticeState.Queued;

    public UiNoticeState State
    {
        get
        {
            if (_queue == null) return StateValue;
            lock (_queue.Gate) return StateValue;
        }
    }

    public void Dispose() => _queue?.Withdraw(this);
}

/// <summary>
/// The process-wide notice queue (P7), game-free: FIFO across every plugin, one notice on screen at a time, a duplicate
/// window, a per-owner rate limit, a bounded queue and drop-on-unload. The Unity half calls <see cref="Next"/> each
/// frame while <see cref="HasWork"/> and presents what it returns; the clock is passed in so the rules are testable.
/// </summary>
internal sealed class UiNoticeQueue
{
    internal readonly object Gate = new();
    private readonly List<UiNoticeHandle> _queued = new();
    private readonly List<KeyValuePair<string, double>> _recent = new();
    private readonly Dictionary<object, List<double>> _rate = new(ReferenceEqualityComparer.Instance);
    private UiNoticeHandle? _showing;
    private double _gapUntil = double.NegativeInfinity;
    private double _hostBusySince = double.NaN;
    private long _serial;

    /// <summary>Raised (outside the lock) when the showing notice must be taken off screen early: disposed or its owner unloaded.</summary>
    internal event Action<UiNoticeHandle>? Withdrawn;

    /// <summary>Notices waiting.</summary>
    internal int QueuedCount
    {
        get { lock (Gate) return _queued.Count; }
    }

    /// <summary>The notice on screen, or null.</summary>
    internal UiNoticeHandle? Showing
    {
        get { lock (Gate) return _showing; }
    }

    /// <summary>True while a notice waits or shows: the Unity half keeps its frame tick only while this holds.</summary>
    internal bool HasWork
    {
        get { lock (Gate) return _showing != null || _queued.Count > 0; }
    }

    /// <summary>
    /// Post a notice. Never throws for bad input: empty text, a duplicate or a rate-limited call returns a handle that
    /// is already <see cref="UiNoticeState.Dropped"/> with <see cref="UiNoticeHandle.DropReason"/> saying why.
    /// </summary>
    internal UiNoticeHandle Enqueue(object? owner, string? header, string? detail, UiNoticeKind kind, double seconds, double now)
    {
        string h = UiNoticeRules.Clean(header, UiNoticeRules.MaxHeaderChars);
        string d = UiNoticeRules.Clean(detail, UiNoticeRules.MaxDetailChars);
        if (h.Length == 0 && d.Length > 0)
        {
            h = d;
            d = "";
        }

        lock (Gate)
        {
            var notice = new UiNoticeHandle(this, owner, h, d, kind, seconds, now, ++_serial);
            if (h.Length == 0) return Drop(notice, "empty");

            string key = UiNoticeRules.DedupeKey(h, d);
            PruneRecent(now);
            foreach (var r in _recent)
                if (string.Equals(r.Key, key, StringComparison.Ordinal))
                    return Drop(notice, "duplicate");

            if (owner != null && !TakeRate(owner, now)) return Drop(notice, "rate-limited");

            _recent.Add(new KeyValuePair<string, double>(key, now));
            if (_queued.Count >= UiNoticeRules.MaxQueued)
            {
                var oldest = _queued[0];
                _queued.RemoveAt(0);
                Drop(oldest, "queue-full");
            }

            _queued.Add(notice);
            return notice;
        }
    }

    /// <summary>
    /// One step of the queue at <paramref name="now"/>. Ends the showing notice when its slot is up; then, when the
    /// screen is free, the gap has passed and the host is not busy (or has been busy longer than
    /// <see cref="UiNoticeRules.MaxHostWaitSeconds"/>), takes the next notice and calls <paramref name="present"/>,
    /// which shows it and returns how many seconds it holds the screen (0 or less: it could not be shown, and the
    /// notice is dropped). Returns the notice that started showing, or null. <paramref name="present"/> runs outside
    /// the lock.
    /// </summary>
    internal UiNoticeHandle? Next(double now, bool hostBusy, Func<UiNoticeHandle, double> present)
    {
        if (present == null) throw new ArgumentNullException(nameof(present));
        UiNoticeHandle? next;
        lock (Gate)
        {
            if (_showing != null)
            {
                if (now < _showing.EndsAt) return null;
                _showing.StateValue = UiNoticeState.Done;
                _showing.PresenterState = null;
                _showing = null;
                _gapUntil = now + UiNoticeRules.GapSeconds;
            }

            if (_queued.Count == 0 || now < _gapUntil)
            {
                if (_queued.Count == 0) _hostBusySince = double.NaN;
                return null;
            }

            if (hostBusy)
            {
                if (double.IsNaN(_hostBusySince)) _hostBusySince = now;
                if (now - _hostBusySince < UiNoticeRules.MaxHostWaitSeconds) return null;
            }

            _hostBusySince = double.NaN;
            next = _queued[0];
            _queued.RemoveAt(0);
            next.StateValue = UiNoticeState.Showing;
            next.ShownAt = now;
            next.EndsAt = double.PositiveInfinity;
            _showing = next;
        }

        double seconds;
        try { seconds = present(next); }
        catch { seconds = 0; }

        lock (Gate)
        {
            if (!ReferenceEquals(_showing, next) || next.StateValue != UiNoticeState.Showing)
                return null; // withdrawn while presenting
            if (!(seconds > 0) || double.IsInfinity(seconds))
            {
                _showing = null;
                next.ShownAt = double.NaN;
                next.EndsAt = double.NaN;
                Drop(next, "not-presented");
                return null;
            }

            next.EndsAt = now + seconds;
            return next;
        }
    }

    /// <summary>Dispose of one notice: a queued one is dropped, the showing one ends (and <see cref="Withdrawn"/> fires).</summary>
    internal void Withdraw(UiNoticeHandle notice)
    {
        bool wasShowing = false;
        lock (Gate)
        {
            if (notice.StateValue == UiNoticeState.Queued)
            {
                _queued.Remove(notice);
                Drop(notice, "disposed");
                return;
            }

            if (notice.StateValue == UiNoticeState.Showing && ReferenceEquals(_showing, notice))
            {
                notice.StateValue = UiNoticeState.Done;
                _showing = null;
                wasShowing = true;
            }
        }

        if (wasShowing) RaiseWithdrawn(notice);
    }

    /// <summary>
    /// The owner unloaded: every queued notice of it is dropped and its showing notice ends. Returns how many were
    /// affected.
    /// </summary>
    internal int DropOwner(object owner)
    {
        if (owner == null) return 0;
        int n = 0;
        UiNoticeHandle? ended = null;
        lock (Gate)
        {
            for (int i = _queued.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_queued[i].Owner, owner)) continue;
                Drop(_queued[i], "owner-unloaded");
                _queued.RemoveAt(i);
                n++;
            }

            if (_showing != null && ReferenceEquals(_showing.Owner, owner))
            {
                _showing.StateValue = UiNoticeState.Done;
                ended = _showing;
                _showing = null;
                n++;
            }

            _rate.Remove(owner);
        }

        if (ended != null) RaiseWithdrawn(ended);
        return n;
    }

    /// <summary>Core teardown: everything queued is dropped, the showing notice ends. The queue is empty afterwards.</summary>
    internal void Clear()
    {
        lock (Gate)
        {
            foreach (var q in _queued) Drop(q, "core-unloaded");
            _queued.Clear();
            if (_showing != null)
            {
                _showing.StateValue = UiNoticeState.Done;
                _showing.PresenterState = null;
                _showing = null;
            }

            _recent.Clear();
            _rate.Clear();
            _gapUntil = double.NegativeInfinity;
            _hostBusySince = double.NaN;
        }
    }

    private void RaiseWithdrawn(UiNoticeHandle notice)
    {
        try { Withdrawn?.Invoke(notice); }
        catch { /* a presenter's hide must not escape into a plugin's Dispose */ }
        notice.PresenterState = null;
    }

    private static UiNoticeHandle Drop(UiNoticeHandle notice, string reason)
    {
        notice.StateValue = UiNoticeState.Dropped;
        notice.DropReason = reason;
        notice.PresenterState = null;
        return notice;
    }

    private void PruneRecent(double now)
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
            if (now - _recent[i].Value >= UiNoticeRules.DedupeWindowSeconds)
                _recent.RemoveAt(i);
    }

    private bool TakeRate(object owner, double now)
    {
        if (!_rate.TryGetValue(owner, out var times))
        {
            times = new List<double>();
            _rate[owner] = times;
        }

        for (int i = times.Count - 1; i >= 0; i--)
            if (now - times[i] >= UiNoticeRules.RateLimitWindowSeconds)
                times.RemoveAt(i);
        if (times.Count >= UiNoticeRules.RateLimitCount) return false;
        times.Add(now);
        return true;
    }
}

/// <summary>
/// The framework band's look (P7), game-free: the stand-in for the game's announcement banner when it is not on screen.
/// Sizes are in the game's 1920x1080 design space (the overlay copies the game's canvas scaler).
/// </summary>
internal static class UiNoticeBand
{
    /// <summary>Top of the band below the top of the screen.</summary>
    internal const float Top = 92f;

    internal const float MinWidth = 420f;

    /// <summary>Widest band: wider text is cut with an ellipsis.</summary>
    internal const float MaxWidth = 1100f;

    internal const float PadX = 30f;

    internal const float PadY = 10f;

    internal const float HeaderSize = 19f;

    internal const float DetailSize = 14f;

    internal const float HeaderHeight = 26f;

    internal const float DetailHeight = 20f;

    /// <summary>The gold rules above and below the band.</summary>
    internal const float RuleThickness = 1f;

    internal const float RuleAlpha = 0.35f;

    /// <summary>The band's ground: the game's near-black warm tint at 86% in the middle, fading out at both ends.</summary>
    internal static readonly PaletteColor Ground = PaletteColor.Hex(0x0a0608, 0.86f);

    /// <summary>Fraction of the width over which each end fades in (the mockup's 18%).</summary>
    internal const float FadeFraction = 0.18f;

    internal const double FadeInSeconds = 0.2;

    internal const double FadeOutSeconds = 0.35;

    /// <summary>Band width for the measured header and detail widths (design pixels).</summary>
    internal static float Width(float headerWidth, float detailWidth)
    {
        float text = Math.Max(Finite(headerWidth), Finite(detailWidth));
        return Math.Max(MinWidth, Math.Min(MaxWidth, text + 2f * PadX));
    }

    /// <summary>Band height: one header line, plus a detail line when there is one.</summary>
    internal static float Height(bool hasDetail) => 2f * PadY + HeaderHeight + (hasDetail ? DetailHeight : 0f);

    /// <summary>The rules' accent for <paramref name="kind"/>: gold for Info, the palette's green, amber and red otherwise.</summary>
    internal static PaletteColor Accent(UiNoticeKind kind) => kind switch
    {
        UiNoticeKind.Success => MenuPalette.OkText,
        UiNoticeKind.Warning => MenuPalette.PendText,
        UiNoticeKind.Error => MenuPalette.Red,
        _ => MenuPalette.Gold,
    };

    /// <summary>
    /// The two rules' color: Info keeps the game's quiet gold at <see cref="RuleAlpha"/>; the other kinds show their
    /// accent stronger (<see cref="KindRuleAlpha"/>) so a warning reads as one at a glance.
    /// </summary>
    internal static PaletteColor RuleColor(UiNoticeKind kind) =>
        Accent(kind).WithAlpha(kind == UiNoticeKind.Info ? RuleAlpha : KindRuleAlpha);

    internal const float KindRuleAlpha = 0.7f;

    /// <summary>Alpha (0..1) of a band shown at <paramref name="shownAt"/> that ends at <paramref name="endsAt"/>.</summary>
    internal static float Alpha(double now, double shownAt, double endsAt)
    {
        if (double.IsNaN(shownAt) || now < shownAt) return 0f;
        double a = 1.0;
        if (FadeInSeconds > 0) a = Math.Min(a, (now - shownAt) / FadeInSeconds);
        if (!double.IsNaN(endsAt) && !double.IsInfinity(endsAt) && FadeOutSeconds > 0)
            a = Math.Min(a, (endsAt - now) / FadeOutSeconds);
        return (float)Math.Max(0.0, Math.Min(1.0, a));
    }

    /// <summary>
    /// Alpha (0..1) of the band's ground at horizontal position <paramref name="u"/> (0..1 across the band): transparent
    /// at both ends, full over the middle, smoothstep between.
    /// </summary>
    internal static float GroundAlpha(float u)
    {
        if (float.IsNaN(u) || u <= 0f || u >= 1f) return 0f;
        float edge = Math.Min(u, 1f - u);
        if (edge >= FadeFraction) return 1f;
        float t = edge / FadeFraction;
        return t * t * (3f - 2f * t);
    }

    private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
}
