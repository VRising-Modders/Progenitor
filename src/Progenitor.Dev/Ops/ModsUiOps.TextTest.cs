#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// TEXTTEST1: <c>mods_ui field_focus | field_blur | field_submit | type | paste | perf</c>. The work is
/// Core's <c>Progenitor.Ui.UiTextTestBridge</c>, reached by reflection (Core grants Dev no
/// <c>InternalsVisibleTo</c>). Dev bridge only: no plugin or network path reaches it. No OS input
/// is sent and no window focus is taken; the test clipboard is cleared on every exit path.
/// </summary>
internal static partial class ModsUiOps
{
    private const string TextTestBridgeType = "Progenitor.Ui.UiTextTestBridge";

    private static Type? TextTestBridge() => typeof(ModsMenu).Assembly.GetType(TextTestBridgeType, throwOnError: false);

    private static object? CallTextTest(string method, params object?[] args)
    {
        var type = TextTestBridge() ?? throw new InvalidOperationException("Core has no " + TextTestBridgeType + " (older than TEXTTEST1)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(TextTestBridgeType + "." + method + " not found");
        try { return mi.Invoke(null, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    private static Dictionary<string, object?> TextTestDict(string method, params object?[] args) =>
        CallTextTest(method, args) as Dictionary<string, object?> ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };

    private static bool IsOk(Dictionary<string, object?> d) => d.TryGetValue("ok", out var v) && v is true;

    private static void ResetTextTest()
    {
        try { CallTextTest("Reset"); } catch { /* best effort: Finish also clears */ }
    }

    private static object ExecuteTextTest(ModsUiArgs.Request request, object before, bool light, Stopwatch clock)
    {
        Func<object> after = () => light ? ModsUiArgs.SkippedStatus(ModsMenu.IsOpen) : ReadStatus();
        try
        {
            switch (request.Action)
            {
                case "field_focus":
                {
                    var r = TextTestDict("Focus", request.Label, request.Pane);
                    r["readback"] = "same-drain";
                    r["field"] = IsOk(r) ? TextTestDict("FieldReadback") : null;
                    return ModsUiResult.Envelope(IsOk(r), request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                }
                case "field_blur":
                case "field_submit":
                {
                    string caption = TextTestCaption();
                    var r = TextTestDict(request.Action == "field_blur" ? "Blur" : "Submit");
                    if (!IsOk(r))
                        return ModsUiResult.Envelope(false, request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                    // TEXTEXPAND1: Done in the expanded editor commits the row the editor is titled with.
                    if (r.TryGetValue("editorTitle", out var title) && title is string t && t.Length > 0) caption = t;
                    // The end of the edit commits through the row's end-edit; read the stored value next frame.
                    return AfterFrames(request.Action, before, after, clock, 2, () =>
                    {
                        r["label"] = caption;
                        r["committed"] = CommittedValue(caption);
                        r["persist"] = CommittedPersist(caption);
                        r["readback"] = "after-2-frames";
                        return (true, r);
                    });
                }
                case "type":
                {
                    var r = TextTestDict("Enqueue", request.Via, request.Text, request.Keys, request.PerFrame);
                    if (!IsOk(r))
                        return ModsUiResult.Envelope(false, request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                    return RunInjected(request, before, after, clock, r, measureFrames: 0);
                }
                case "paste":
                {
                    var r = TextTestDict("BeginPaste", request.Text, request.Repeat, request.Length, request.Seed);
                    if (!IsOk(r))
                        return ModsUiResult.Envelope(false, request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                    return RunInjected(request, before, after, clock, r, request.Frames);
                }
                case "perf":
                    return RunPerf(request, before, after, clock);
                case "field_expand":
                {
                    // The dialog is built now and laid out by the runtime's pass; read it two frames on.
                    var r = TextTestDict("Expand", request.Label);
                    if (!IsOk(r))
                        return ModsUiResult.Envelope(false, request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                    return AfterFrames(request.Action, before, after, clock, 2, () =>
                    {
                        r["field"] = TextTestDict("FieldReadback");
                        r["readback"] = "after-2-frames";
                        return (true, r);
                    });
                }
                case "field_scroll":
                {
                    var r = TextTestDict("Scroll", request.Index ?? 0, request.To);
                    return ModsUiResult.Envelope(IsOk(r), request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
                }
                case "field_cancel":
                {
                    var r = TextTestDict("Cancel");
                    return AfterFrames(request.Action, before, after, clock, 1, () =>
                    {
                        r["readback"] = "next-frame";
                        return (IsOk(r), r);
                    });
                }
                case "field_apply":
                    return RunApply(request, before, after, clock);
            }
        }
        catch (Exception e)
        {
            ResetTextTest();
            return ModsUiResult.Envelope(false, request.Action, before, after(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds);
        }

        throw new ArgumentException("not a text test action: " + request.Action);
    }

    private static string TextTestCaption()
    {
        try
        {
            var d = TextTestDict("PerfFrame");
            return d.TryGetValue("focused", out var f) && f is string s ? s : "";
        }
        catch { return ""; }
    }

    /// <summary>Run <paramref name="finish"/> after <paramref name="frames"/> drains and publish its result.</summary>
    private static object AfterFrames(
        string action, object before, Func<object> after, Stopwatch clock, int frames,
        Func<(bool ok, object detail)> finish)
    {
        var deferred = new DeferredResult(ModsUiArgs.NextDrainWaitMs);
        int left = frames;
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                if (--left > 0)
                {
                    MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
                    return;
                }
                var (ok, detail) = finish();
                deferred.SetResult(ModsUiResult.Envelope(ok, action, before, after(), detail, clock.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(ModsUiResult.Envelope(false, action, before, ModsUiResult.EmptyStatus(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds));
            }
        }
        MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
        return deferred;
    }

    /// <summary>
    /// Let the pump consume the queued input (one frame per <c>perFrame</c> items), wait one more
    /// frame so TMP lays the final text out and the caret is placed on it, then report. With
    /// <paramref name="measureFrames"/> the frames from the first injected one are also sampled.
    /// </summary>
    private static object RunInjected(
        ModsUiArgs.Request request, object before, Func<object> after, Stopwatch clock,
        Dictionary<string, object?> started, int measureFrames)
    {
        var deferred = new DeferredResult(ModsUiArgs.TextTestBudgetMs + 2_000);
        var game = Stopwatch.StartNew();
        var perf = measureFrames > 0 ? new PerfWindow() : null;
        // The baseline: read in this drain, before the pump injects anything this frame.
        perf?.Sample(TextTestDict("PerfFrame"));
        int framesRun = 0, settle = 0;
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                {
                    ResetTextTest();
                    return;
                }
                framesRun++;
                perf?.Sample(TextTestDict("PerfFrame"));
                int pending = Convert.ToInt32(CallTextTest("Pending"));
                bool timedOut = game.ElapsedMilliseconds > ModsUiArgs.TextTestBudgetMs;
                bool measuring = perf != null && perf.Samples < measureFrames + 1;
                if (!timedOut && (pending > 0 || settle++ < 1 || measuring))
                {
                    MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
                    return;
                }

                var result = TextTestDict("Finish");
                result["started"] = started;
                result["frames"] = framesRun;
                result["perFrame"] = request.PerFrame;
                if (perf != null) result["perf"] = perf.Report();
                if (timedOut) result["error"] = "stopped after " + ModsUiArgs.TextTestBudgetMs + " ms of game time";
                deferred.SetResult(ModsUiResult.Envelope(!timedOut, request.Action, before, after(), result, clock.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                ResetTextTest();
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(ModsUiResult.Envelope(false, request.Action, before, ModsUiResult.EmptyStatus(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds));
            }
        }

        try { MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred); }
        catch
        {
            ResetTextTest();
            throw;
        }
        return deferred;
    }

    /// <summary>
    /// TEXTLIMIT1 <c>field_apply</c>: press the admin row's Apply, then wait (in game time, polling each
    /// drain) until the row is no longer waiting for the server — saved or refused — and report it.
    /// </summary>
    private static object RunApply(ModsUiArgs.Request request, object before, Func<object> after, Stopwatch clock)
    {
        var sent = request.Value == null ? TextTestDict("Apply", request.Label) : TextTestDict("ApplyText", request.Label, request.Value);
        if (!IsOk(sent))
            return ModsUiResult.Envelope(false, request.Action, before, after(), sent, clock.Elapsed.TotalMilliseconds);
        var deferred = new DeferredResult(ModsUiArgs.TextTestBudgetMs + 2_000);
        var game = Stopwatch.StartNew();
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                var now = TextTestDict("ApplyStatus", request.Label);
                bool waiting = now.TryGetValue("waiting", out var w) && w is true;
                // The request itself gives up after 8 s; 12 s here covers it and the next frames.
                if (waiting && game.ElapsedMilliseconds < 12_000)
                {
                    MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
                    return;
                }
                now["sent"] = sent;
                now["readback"] = waiting ? "still waiting after 12 s" : "answered";
                deferred.SetResult(ModsUiResult.Envelope(!waiting, request.Action, before, after(), now, clock.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(ModsUiResult.Envelope(false, request.Action, before, ModsUiResult.EmptyStatus(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds));
            }
        }
        MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
        return deferred;
    }

    private static object RunPerf(ModsUiArgs.Request request, object before, Func<object> after, Stopwatch clock)
    {
        var deferred = new DeferredResult(ModsUiArgs.TextTestBudgetMs + 2_000);
        var game = Stopwatch.StartNew();
        var perf = new PerfWindow();
        perf.Sample(TextTestDict("PerfFrame"));
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                perf.Sample(TextTestDict("PerfFrame"));
                bool timedOut = game.ElapsedMilliseconds > ModsUiArgs.TextTestBudgetMs;
                if (perf.Samples < request.Frames + 1 && !timedOut)
                {
                    MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
                    return;
                }
                var report = perf.Report();
                report["requestedFrames"] = request.Frames;
                if (timedOut) report["stoppedEarly"] = "game-time budget " + ModsUiArgs.TextTestBudgetMs + " ms";
                report["field"] = TextTestDict("FieldReadback");
                deferred.SetResult(ModsUiResult.Envelope(true, request.Action, before, after(), report, clock.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                if (!deferred.Completion.Task.IsCompleted)
                    deferred.SetResult(ModsUiResult.Envelope(false, request.Action, before, ModsUiResult.EmptyStatus(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds));
            }
        }
        MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
        return deferred;
    }

    /// <summary>
    /// The stored value of the settings row captioned <paramref name="caption"/>, from the page's own
    /// readback (<c>page-setting.value</c>), or null when no such row is drawn.
    /// </summary>
    private static object? CommittedValue(string caption) => RowProperty(caption, "value", raw: false);

    /// <summary>
    /// SAVEHOLD1: the row's <c>page-setting.persist</c> readback (how the last write was saved and
    /// whether the cfg on disk holds it), or null when the row has not been written.
    /// </summary>
    private static object? CommittedPersist(string caption) => RowProperty(caption, "persist", raw: true);

    private static object? RowProperty(string caption, string property, bool raw)
    {
        if (string.IsNullOrWhiteSpace(caption)) return null;
        try
        {
            var rows = ModsMenu.ReadVisibleWidgets(null);
            foreach (var row in rows)
            {
                var element = JsonSerializer.SerializeToElement(row, ModsUiResult.JsonOptions);
                var found = FindValue(element, caption.Trim(), property, 0);
                if (found is JsonElement el)
                {
                    if (raw) return el.Clone();
                    return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                }
            }
        }
        catch { /* no readback */ }
        return null;
    }

    private static JsonElement? FindValue(JsonElement e, string caption, string property, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            bool named = false;
            foreach (var key in new[] { "displayName", "label" })
            {
                if (e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String
                    && string.Equals(n.GetString(), caption, StringComparison.OrdinalIgnoreCase))
                    named = true;
            }
            if (named && e.TryGetProperty(property, out var v))
                return v;
            foreach (var p in e.EnumerateObject())
            {
                var inner = FindValue(p.Value, caption, property, depth + 1);
                if (inner != null) return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var inner = FindValue(item, caption, property, depth + 1);
                if (inner != null) return inner;
            }
        }
        return null;
    }

    /// <summary>
    /// Frame samples from <c>UiTextTestBridge.PerfFrame</c>. A sample taken at a frame's drain
    /// describes the frame before it, so the first sample (the frame before the window) is a
    /// baseline for the counters only.
    /// </summary>
    private sealed class PerfWindow
    {
        private readonly List<long> _interval = new();
        private readonly List<long> _frame = new();
        private readonly List<long> _outside = new();
        private Dictionary<string, object?>? _first;
        private Dictionary<string, object?>? _last;
        private long _worstMs = -1;
        private string _worstName = "";
        private readonly Dictionary<string, int> _worstByName = new(StringComparer.Ordinal);

        internal int Samples { get; private set; }

        internal void Sample(Dictionary<string, object?> d)
        {
            Samples++;
            if (_first == null)
            {
                _first = d;
                _last = d;
                return;
            }
            _last = d;
            _interval.Add(Num(d, "intervalMs"));
            _frame.Add(Num(d, "frameMs"));
            _outside.Add(Num(d, "outsideMs"));
            long h = Num(d, "worstHandlerMs");
            string name = d.TryGetValue("worstHandler", out var n) && n is string s ? s : "";
            if (h > _worstMs) { _worstMs = h; _worstName = name; }
            if (name.Length > 0 && h > 0) _worstByName[name] = _worstByName.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        internal Dictionary<string, object?> Report()
        {
            var r = new Dictionary<string, object?>
            {
                ["frames"] = _interval.Count,
                ["frameTimeMs"] = Stats(_interval),
                ["ourFrameMs"] = Stats(_frame),
                ["outsideMs"] = Stats(_outside),
                ["worstHandler"] = _worstName,
                ["worstHandlerMs"] = Math.Max(0, _worstMs),
                ["worstHandlerFrames"] = _worstByName,
                ["note"] = "frameTimeMs is the frame interval; ourFrameMs is Core's frame handlers; outsideMs is the rest of the frame (the game, uGUI/TMP layout and rendering)",
            };
            if (_first != null && _last != null)
            {
                r["focusedField"] = new Dictionary<string, object?>
                {
                    ["caption"] = _last.TryGetValue("focused", out var f) ? f : "",
                    ["textDirtyBeforePump"] = Num(_last, "textDirtyBeforePump") - Num(_first, "textDirtyBeforePump"),
                    ["textDirtyByPump"] = Num(_last, "textDirtyByPump") - Num(_first, "textDirtyByPump"),
                    ["caretMeasureCalls"] = Num(_last, "measureCalls") - Num(_first, "measureCalls"),
                    ["caretWrites"] = Num(_last, "caretWrites") - Num(_first, "caretWrites"),
                    ["scrollWrites"] = Num(_last, "scrollWrites") - Num(_first, "scrollWrites"),
                    ["layoutRebuilds"] = "not measured (uGUI's LayoutRebuilder has no counter on this build)",
                };
            }
            return r;
        }

        private static Dictionary<string, object?> Stats(List<long> values)
        {
            if (values.Count == 0) return new Dictionary<string, object?> { ["count"] = 0 };
            var sorted = new List<long>(values);
            sorted.Sort();
            double sum = 0;
            foreach (long v in sorted) sum += v;
            int p95 = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
            if (p95 < 0) p95 = 0;
            return new Dictionary<string, object?>
            {
                ["count"] = sorted.Count,
                ["min"] = sorted[0],
                ["avg"] = Math.Round(sum / sorted.Count, 2),
                ["p95"] = sorted[p95],
                ["max"] = sorted[sorted.Count - 1],
            };
        }

        private static long Num(Dictionary<string, object?> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch { return 0; }
        }
    }
}
#endif
