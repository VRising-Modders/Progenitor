using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// WIPECONFIRM1 (decided 2026-10-06): the game's own scheduled wipe (<c>ResetDaysInterval</c>, <c>DayOfReset</c>),
/// set by a date and confirmed with its exact consequence. Game-free, so the conversion and the confirmation's text and
/// gating have tests.
///
/// <para>The game's rule (research server-browser-filter-settings.md §5.7, <see cref="ServerRules.ResetDate"/>): the
/// wipe date is the world's start date (<c>StartDate.json</c>, its date part) plus the interval in days, moved forward to
/// the next Wipe on weekday (Any: no move). At server start only, if the server's local clock is on or after that date,
/// the game moves the save to <c>&lt;save&gt;_&lt;ticks&gt;</c> and starts a fresh world, which gets a new start date.</para>
/// </summary>
internal static class WipePlan
{
    /// <summary>The word a change that makes a wipe due, or earlier, needs typed.</summary>
    internal const string WordWipe = "WIPE";

    /// <summary>The word any other change that keeps or turns on a wipe needs typed.</summary>
    internal const string WordConfirm = "CONFIRM";

    internal const string Heading = "Change the scheduled wipe?";
    internal const string ConfirmCaption = "Change the wipe";

    /// <summary>
    /// The interval that makes the wipe fall on <paramref name="picked"/> (its date): whole days from the world's start
    /// date, at least 1. Null with <paramref name="refusal"/> when the start date is unknown, or the date is not after the
    /// start, or it is before today (the server's date).
    /// </summary>
    internal static int? IntervalFor(DateTime? startDate, DateTime picked, DateTime todayServer, out string refusal)
    {
        refusal = "";
        if (startDate is not { } start)
        {
            refusal = "This world's start date is not known yet (no StartDate.json), so a wipe date can't be set.";
            return null;
        }

        var day = picked.Date;
        if (day < todayServer.Date)
        {
            refusal = "Pick today or a later date.";
            return null;
        }

        int days = (int)Math.Round((day - start.Date).TotalDays);
        if (days < 1)
        {
            refusal = "Pick a date after the world's start (" + ServerRules.FormatDate(start.Date) + ").";
            return null;
        }

        if (days > ServerRules.ResetDaysMax)
        {
            refusal = "That is more than " + ServerRules.ResetDaysMax.ToString(CultureInfo.InvariantCulture) + " days after the world's start.";
            return null;
        }

        return days;
    }

    /// <summary>The Wipe on value that keeps <paramref name="picked"/> exact: the current one when it is Any or that weekday, else Any.</summary>
    internal static string DayFor(DateTime picked, string? currentDay)
    {
        string current = string.IsNullOrWhiteSpace(currentDay) ? "Any" : currentDay!.Trim();
        if (string.Equals(current, "Any", StringComparison.OrdinalIgnoreCase)) return "Any";
        return string.Equals(current, picked.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase) ? current : "Any";
    }

    /// <summary><c>every 34 days on Wednesday</c>, <c>every 30 days on Any</c>, or <c>off</c>.</summary>
    internal static string Describe(int interval, string? day) =>
        interval <= 0 ? "off" : "every " + interval.ToString(CultureInfo.InvariantCulture) + (interval == 1 ? " day" : " days") + " on "
            + (string.IsNullOrWhiteSpace(day) ? "Any" : day!.Trim());

    /// <summary>The line under the date picker: <c>= every 34 days from the world's start (Sep 10, 2026)</c>.</summary>
    internal static string IntervalNote(int interval, string? day, DateTime? start) =>
        interval <= 0 ? "No scheduled wipe."
            : "= " + Describe(interval, day) + (start is { } s ? " from the world's start (" + ServerRules.FormatDate(s) + ")" : "");

    /// <summary><c>in 7 days 4 hours</c>, <c>in 3 hours</c>, <c>now</c>.</summary>
    internal static string Until(DateTime fromLocal, DateTime toLocal)
    {
        var span = toLocal - fromLocal;
        if (span <= TimeSpan.Zero) return "now";
        int days = (int)Math.Floor(span.TotalDays);
        int hours = span.Hours;
        if (days > 0) return "in " + days + (days == 1 ? " day" : " days") + (hours > 0 ? " " + hours + (hours == 1 ? " hour" : " hours") : "");
        int h = (int)Math.Floor(span.TotalHours);
        if (h > 0) return "in " + h + (h == 1 ? " hour" : " hours");
        int m = Math.Max(1, (int)Math.Floor(span.TotalMinutes));
        return "in " + m + (m == 1 ? " minute" : " minutes");
    }

    /// <summary>What a change to the wipe settings leads to, and what the confirmation asks.</summary>
    internal sealed class Outcome
    {
        public string Summary = "";
        public DateTime? Before;
        public DateTime? After;
        public bool Due;
        public bool Earlier;
        public bool Off;

        /// <summary>The word to type: <see cref="WordWipe"/>, <see cref="WordConfirm"/>, or empty (a click is enough).</summary>
        public string Word = "";

        /// <summary>The whole confirmation as text (readback, tests): the facts as <c>Label: value</c> lines.</summary>
        public string Body = "";

        /// <summary>WIPEPOLISH1: what the dialog draws, as label/value lines; the warning (if any) is the last, in the danger tone.</summary>
        public List<DialogFact> Facts = new();

        /// <summary>WIPEPOLISH1: the due warning, or empty.</summary>
        public string Warning = "";
    }

    /// <summary>
    /// The confirmation for a change of the wipe settings from (<paramref name="intervalBefore"/>, <paramref name="dayBefore"/>)
    /// to (<paramref name="intervalAfter"/>, <paramref name="dayAfter"/>), on a server whose local time now is
    /// <paramref name="nowServer"/> (in <paramref name="serverZone"/>). Due (the next start wipes), earlier than before, or
    /// turning wipes on asks for <see cref="WordWipe"/>; turning wipes off or moving the date later needs only a click; any
    /// other change (one that keeps the date) asks for <see cref="WordConfirm"/>.
    /// </summary>
    internal static Outcome For(
        DateTime? startDate, int intervalBefore, string? dayBefore, int intervalAfter, string? dayAfter,
        DateTime nowServer, TimeZoneInfo? serverZone, TimeZoneInfo? playerZone, string? saveName)
    {
        var o = new Outcome
        {
            Summary = "Scheduled wipe: " + Describe(intervalBefore, dayBefore) + " → " + Describe(intervalAfter, dayAfter),
            Off = intervalAfter <= 0,
        };
        if (startDate is { } s)
        {
            o.Before = intervalBefore > 0 ? ServerRules.ResetDate(s, intervalBefore, dayBefore) : null;
            o.After = intervalAfter > 0 ? ServerRules.ResetDate(s, intervalAfter, dayAfter) : null;
        }

        o.Due = !o.Off && (o.After is not { } a || a <= nowServer);
        o.Earlier = !o.Off && (o.Before is not { } b || (o.After is { } a2 && a2 < b));
        o.Word = o.Off ? "" : o.Due || o.Earlier ? WordWipe : (o.After is { } a3 && o.Before is { } b3 && a3 > b3) ? "" : WordConfirm;

        // WIPEPOLISH1 (live 2026-10-06: a centered wall of text): label/value lines, left-aligned, then the due warning
        // on its own line in the danger tone, above the typed-word field.
        o.Facts.Add(new DialogFact("Change", Describe(intervalBefore, dayBefore) + " → " + Describe(intervalAfter, dayAfter)));
        if (o.Off) o.Facts.Add(new DialogFact("Wipe date", "none; the world keeps running"));
        else if (o.After is { } wipe)
        {
            o.Facts.Add(new DialogFact("Wipe date", WhenLine(wipe, serverZone, playerZone)));
            o.Facts.Add(new DialogFact("Time until", Until(nowServer, wipe)));
        }
        else o.Facts.Add(new DialogFact("Wipe date", "not known: this world has no start date yet, so the next start may wipe it",
            DialogFactTone.Warn));

        if (startDate is { } start)
        {
            int age = Math.Max(0, (int)Math.Floor((nowServer.Date - start.Date).TotalDays));
            o.Facts.Add(new DialogFact("World", "started " + ServerRules.FormatDate(start) + " · " + age + (age == 1 ? " day old" : " days old")));
        }

        if (!o.Off)
            o.Facts.Add(new DialogFact("What happens", "the game archives this world to " + ArchiveName(saveName)
                + " and starts a new world, with a new start date; the countdown starts over"));

        if (o.Due && startDate is { } st)
        {
            int age = Math.Max(0, (int)Math.Floor((nowServer.Date - st.Date).TotalDays));
            o.Warning = "This world is already " + age + (age == 1 ? " day" : " days") + " old: the NEXT restart wipes it.";
            o.Facts.Add(new DialogFact("Warning", o.Warning, DialogFactTone.Danger));
        }

        var sb = new StringBuilder();
        foreach (var f in o.Facts) sb.Append(sb.Length > 0 ? "\n" : "").Append(f.Label).Append(": ").Append(f.Value);
        if (o.Word.Length > 0) sb.Append("\nType ").Append(o.Word).Append(" to confirm.");
        o.Body = sb.ToString();
        return o;
    }

    /// <summary>
    /// WIPEPOLISH1: where the game moves the world: <c>Saves/v4/&lt;SaveName&gt;_&lt;ticks at wipe&gt;</c> under the server's
    /// persistent data folder (the save folder's own path with the wipe's ticks appended, research §5.7), with the server's
    /// real SaveName. Unknown SaveName: the placeholder.
    /// </summary>
    internal static string ArchiveName(string? saveName) =>
        "Saves/v4/" + (string.IsNullOrWhiteSpace(saveName) ? "<SaveName>" : saveName!.Trim()) + "_<ticks at wipe>";

    /// <summary>WIPEPOLISH1: the hint under the field while what is typed is not the word.</summary>
    internal static string MismatchHint(string word) => UiDialogs.ConfirmWordHint(word);

    /// <summary><c>Wed, Oct 14, 2026, at the first server start after 12:00 AM (server time)</c>, plus the player's time when it differs.</summary>
    internal static string WhenLine(DateTime wipeDate, TimeZoneInfo? serverZone, TimeZoneInfo? playerZone)
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        string line = wipeDate.ToString("ddd, MMM d, yyyy", us) + ", first server start after 12:00 AM (server time)";
        var server = serverZone ?? TimeZoneInfo.Utc;
        var player = playerZone ?? server;
        try
        {
            var utc = ResetCountdown.LocalToUtc(DateTime.SpecifyKind(wipeDate.Date, DateTimeKind.Unspecified), server, out _);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, player);
            if (server.GetUtcOffset(utc) != player.GetUtcOffset(utc))
                line += " (" + local.ToString("ddd, MMM d, h:mm tt", us) + " your time)";
        }
        catch { /* the server's line stands */ }

        return line;
    }

    /// <summary>The Pending changes warning line for a change that makes a wipe due.</summary>
    internal static string? PendingWarning(DateTime? startDate, int interval, string? day, DateTime nowServer)
    {
        if (interval <= 0 || startDate is not { } s) return null;
        if (ServerRules.ResetDate(s, interval, day) is not { } reset || reset > nowServer) return null;
        int age = Math.Max(0, (int)Math.Floor((nowServer.Date - s.Date).TotalDays));
        return "Warning: this world is " + age + (age == 1 ? " day" : " days") + " old; the next restart wipes it.";
    }
}
