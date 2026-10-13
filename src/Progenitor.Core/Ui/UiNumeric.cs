using System;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Culture-safe number parse/format for numeric fields and sliders. Always
/// <see cref="CultureInfo.InvariantCulture"/> on the way out. On the way in, a
/// lone comma is a decimal separator so a locale that types <c>1,5</c> cannot
/// become <c>15</c> or fail closed.
/// </summary>
internal static class UiNumericParse
{
    internal static bool TryParse(string? text, out double value, out string error)
    {
        value = 0;
        error = "";
        if (text == null)
        {
            error = "empty";
            return false;
        }

        string s = text.Trim();
        if (s.Length == 0)
        {
            error = "empty";
            return false;
        }

        s = NormalizeDecimal(s);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            error = "not a number";
            value = 0;
            return false;
        }

        return true;
    }

    internal static string Format(double value, int decimals)
    {
        if (decimals <= 0)
            return Math.Round(value, 0, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("0.");
        for (int i = 0; i < decimals; i++)
            sb.Append('#');
        return value.ToString(sb.ToString(), CultureInfo.InvariantCulture);
    }

    internal static double Clamp(double value, double min, double max)
    {
        if (min > max)
        {
            double t = min;
            min = max;
            max = t;
        }

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    internal static double Snap(double value, double min, double max, double step)
    {
        value = Clamp(value, min, max);
        if (step <= 0) return value;
        // SETTINGS1: a field bounded only by its type (min = long.MinValue, -double.MaxValue) snaps
        // from zero; from such a min the grid is lost to rounding or overflows to infinity.
        double origin = Math.Abs(min) <= 1e15 ? min : 0;
        double n = Math.Round((value - origin) / step, MidpointRounding.AwayFromZero);
        double snapped = origin + n * step;
        if (double.IsNaN(snapped) || double.IsInfinity(snapped)) return value;
        return Clamp(snapped, min, max);
    }

    /// <summary>
    /// One comma and no period → decimal. One period and no comma → decimal.
    /// Both present: the last one is the decimal, the other is a group separator
    /// and is stripped. Neither: integer.
    /// </summary>
    internal static string NormalizeDecimal(string text)
    {
        int lastComma = text.LastIndexOf(',');
        int lastPeriod = text.LastIndexOf('.');
        if (lastComma < 0 && lastPeriod < 0) return text;

        char decimalSep;
        if (lastComma < 0) decimalSep = '.';
        else if (lastPeriod < 0) decimalSep = ',';
        else decimalSep = lastComma > lastPeriod ? ',' : '.';

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ') continue;
            if (c == ',' || c == '.')
            {
                if (c == decimalSep && i == (decimalSep == ',' ? lastComma : lastPeriod))
                    sb.Append('.');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Numeric field + stepper. Typed text commits on Apply / Enter
/// (<see cref="UiCommitPolicy.OnSubmit"/>). A stepper click is a discrete write
/// (<see cref="UiCommitPolicy.Immediate"/>).
/// </summary>
internal sealed class UiNumericState
{
    internal const UiCommitPolicy TypedCommit = UiCommitPolicy.OnSubmit;
    internal const UiCommitPolicy StepperCommit = UiCommitPolicy.Immediate;

    public UiNumericState(double value, double min, double max, double step, int decimals, string? units)
    {
        Min = min;
        Max = max;
        Step = step > 0 ? step : 1;
        Decimals = decimals < 0 ? 0 : decimals;
        Units = units ?? "";
        Committed = UiNumericParse.Snap(value, Min, Max, Step);
        Draft = Format(Committed);
    }

    public double Min { get; }

    public double Max { get; }

    public double Step { get; }

    public int Decimals { get; }

    public string Units { get; }

    public double Committed { get; private set; }

    public string Draft { get; private set; }

    public bool DraftDirty { get; private set; }

    public string DisplayCommitted => Format(Committed);

    public void SetDraft(string? text)
    {
        Draft = text ?? "";
        DraftDirty = true;
    }

    /// <summary>Parse, snap, commit. Returns false and leaves the committed value when the draft is unreadable.</summary>
    public bool TryCommitDraft(out string error)
    {
        if (!UiNumericParse.TryParse(Draft, out double parsed, out error))
        {
            Draft = Format(Committed);
            DraftDirty = false;
            return false;
        }

        double next = UiNumericParse.Snap(parsed, Min, Max, Step);
        bool changed = !Near(Committed, next);
        Committed = next;
        Draft = Format(Committed);
        DraftDirty = false;
        error = "";
        return changed;
    }

    public void CancelDraft()
    {
        Draft = Format(Committed);
        DraftDirty = false;
    }

    /// <summary>Stepper: immediate commit of one or more steps.</summary>
    public bool StepBy(int steps)
    {
        if (steps == 0) return false;
        double next = UiNumericParse.Snap(Committed + steps * Step, Min, Max, Step);
        if (Near(Committed, next)) return false;
        Committed = next;
        Draft = Format(Committed);
        DraftDirty = false;
        return true;
    }

    public bool SetCommitted(double value)
    {
        double next = UiNumericParse.Snap(value, Min, Max, Step);
        if (Near(Committed, next))
        {
            Draft = Format(Committed);
            DraftDirty = false;
            return false;
        }

        Committed = next;
        Draft = Format(Committed);
        DraftDirty = false;
        return true;
    }

    public string Format(double value)
    {
        string n = UiNumericParse.Format(value, Decimals);
        return Units.Length == 0 ? n : n;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
}
