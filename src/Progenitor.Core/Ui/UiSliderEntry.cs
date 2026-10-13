using System;

namespace Progenitor.Ui;

/// <summary>
/// Slider plus exact numeric entry, kept in sync. Mouse drag updates
/// <see cref="Preview"/> only and commits once on <see cref="EndDrag"/> —
/// never a write per frame. Keyboard nudges and a successful exact entry
/// commit immediately / on submit respectively.
/// </summary>
internal sealed class UiSliderEntryState
{
    internal const UiCommitPolicy DragCommit = UiCommitPolicy.OnRelease;
    internal const UiCommitPolicy KeyboardCommit = UiCommitPolicy.Immediate;
    internal const UiCommitPolicy ExactCommit = UiCommitPolicy.OnSubmit;

    public UiSliderEntryState(float value, float min, float max, float step)
    {
        Min = min;
        Max = max;
        Step = step > 0f ? step : 0f;
        float snapped = Snap(value);
        Committed = snapped;
        Preview = snapped;
        Draft = UiNumericParse.Format(snapped, StepDecimals(step));
    }

    public float Min { get; }

    public float Max { get; }

    public float Step { get; }

    public float Committed { get; private set; }

    public float Preview { get; private set; }

    public string Draft { get; private set; }

    public bool Dragging { get; private set; }

    public void BeginDrag()
    {
        Dragging = true;
        Preview = Committed;
    }

    /// <summary>Local preview. Does not commit.</summary>
    public void DragTo(float value)
    {
        Preview = Snap(value);
        Draft = Format(Preview);
    }

    /// <summary>Commit the preview if it differs. Returns true when a write is owed.</summary>
    public bool EndDrag()
    {
        Dragging = false;
        if (Near(Preview, Committed))
        {
            Preview = Committed;
            Draft = Format(Committed);
            return false;
        }

        Committed = Preview;
        Draft = Format(Committed);
        return true;
    }

    /// <summary>Arrow / step key. Discrete, so it commits now.</summary>
    public bool Nudge(int steps)
    {
        if (steps == 0) return false;
        float delta = Step > 0f ? Step * steps : (Max - Min) * 0.01f * steps;
        float next = Snap(Committed + delta);
        if (Near(next, Committed)) return false;
        Committed = next;
        Preview = next;
        Draft = Format(Committed);
        return true;
    }

    public bool SetCommitted(float value)
    {
        float next = Snap(value);
        if (Near(next, Committed) && Near(next, Preview))
        {
            Draft = Format(Committed);
            return false;
        }

        Committed = next;
        Preview = next;
        Draft = Format(Committed);
        return true;
    }

    public bool TryCommitDraft(string? text, out string error)
    {
        if (!UiNumericParse.TryParse(text, out double parsed, out error))
        {
            Draft = Format(Committed);
            return false;
        }

        float next = Snap((float)parsed);
        Draft = Format(next);
        error = "";
        if (Near(next, Committed)) return false;
        Committed = next;
        Preview = next;
        return true;
    }

    public float Snap(float value)
    {
        double snapped = UiNumericParse.Snap(value, Min, Max, Step);
        return (float)snapped;
    }

    public string Format(float value) => UiNumericParse.Format(value, StepDecimals(Step));

    internal static int StepDecimals(float step)
    {
        if (step <= 0f) return 3;
        int d = 0;
        double s = step;
        while (d < 6 && Math.Abs(s - Math.Round(s)) > 1e-6)
        {
            s *= 10;
            d++;
        }

        return d;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
}
