namespace Progenitor.Sample;

internal enum FrostDashEdge { None, Start, End }

/// <summary>
/// Pairs observed dash starts with ends. Attaching or resetting during a dash
/// waits for that dash to finish before allowing a new pair.
/// </summary>
internal sealed class FrostDashEdgeTracker
{
    private bool _wasDashing;
    private bool _startedHere;

    internal FrostDashEdgeTracker(bool initiallyDashing) => Reset(initiallyDashing);

    internal FrostDashEdge Observe(bool isDashing)
    {
        if (isDashing == _wasDashing) return FrostDashEdge.None;
        _wasDashing = isDashing;
        if (isDashing)
        {
            _startedHere = true;
            return FrostDashEdge.Start;
        }

        bool paired = _startedHere;
        _startedHere = false;
        return paired ? FrostDashEdge.End : FrostDashEdge.None;
    }

    /// <summary>Cancel any pending end and establish the current observed state.</summary>
    internal void Reset(bool initiallyDashing)
    {
        _wasDashing = initiallyDashing;
        _startedHere = false;
    }
}
