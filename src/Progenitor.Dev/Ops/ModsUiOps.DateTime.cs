#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// JOINFLOW1: <c>mods_ui datetime_open | datetime_read | datetime_month | datetime_day | datetime_time |
/// datetime_done | datetime_cancel</c>. The work is Core's <c>Progenitor.Ui.UiDateTimeBridge</c>, reached by
/// reflection (Core grants Dev no <c>InternalsVisibleTo</c>); each action calls the handler the matching
/// button calls. Dev bridge only, main thread, client only. No OS input is sent and no focus is taken.
/// </summary>
internal static partial class ModsUiOps
{
    private const string DateTimeBridgeType = "Progenitor.Ui.UiDateTimeBridge";

    private static Dictionary<string, object?> CallDateTime(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(DateTimeBridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + DateTimeBridgeType + " (older than JOINFLOW1)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(DateTimeBridgeType + "." + method + " not found");
        object? result;
        try { result = mi.Invoke(null, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
        return result as Dictionary<string, object?>
            ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };
    }

    private static int? DateTimeInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int s)) return s;
        throw new ArgumentException("args." + name + " must be a whole number");
    }

    private static object ExecuteDateTime(ModsUiArgs.Request request, object before, bool light, Stopwatch clock)
    {
        Func<object> after = () => light ? ModsUiArgs.SkippedStatus(ModsMenu.IsOpen) : ReadStatus();
        Dictionary<string, object?> r;
        try
        {
            var args = request.Raw;
            switch (request.Action)
            {
                case "datetime_open":
                    if (string.IsNullOrWhiteSpace(request.Label))
                        throw new ArgumentException("args.label is required: the date-time row's caption (its display name)");
                    if (!ModsMenu.IsOpen) throw new ArgumentException(ModsUiArgs.WorkspaceClosedError);
                    r = CallDateTime("Open", request.Label);
                    break;
                case "datetime_read":
                    r = CallDateTime("Read");
                    break;
                case "datetime_month":
                    r = CallDateTime("Month", DateTimeInt(args, "delta"), DateTimeInt(args, "year"), DateTimeInt(args, "month"));
                    break;
                case "datetime_day":
                    r = CallDateTime("Day", DateTimeInt(args, "day") ?? throw new ArgumentException("args.day is required (1-31)"));
                    break;
                case "datetime_time":
                {
                    int? hour = DateTimeInt(args, "hour"), minute = DateTimeInt(args, "minute"), second = DateTimeInt(args, "second");
                    if (hour == null && minute == null && second == null)
                        throw new ArgumentException("give at least one of args.hour, args.minute, args.second");
                    r = CallDateTime("Time", hour, minute, second);
                    break;
                }
                case "datetime_done":
                    r = CallDateTime("Done");
                    break;
                case "datetime_cancel":
                    r = CallDateTime("Cancel");
                    break;
                default:
                    throw new ArgumentException("not a date-time action: " + request.Action);
            }
        }
        catch (Exception e)
        {
            return ModsUiResult.Envelope(false, request.Action, before, after(), new { error = e.Message }, clock.Elapsed.TotalMilliseconds);
        }

        bool ok = r.TryGetValue("ok", out var v) && v is true;
        r["readback"] = "same-drain";
        return ModsUiResult.Envelope(ok, request.Action, before, after(), r, clock.Elapsed.TotalMilliseconds);
    }
}
#endif
