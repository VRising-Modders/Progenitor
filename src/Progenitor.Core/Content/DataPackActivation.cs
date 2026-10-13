using System;

namespace Progenitor.Content;

/// <summary>Serial bounded activation and reverse rollback. A failed operation is undone too.
/// Time is a soft budget: an individual operation always runs to completion.</summary>
internal sealed class DataPackActivation
{
    public const int ItemsPerFrame = 8;
    public const double MillisecondsPerFrame = 4;
    private readonly int _count;
    private readonly Action<int> _apply;
    private readonly Action<int> _undo;
    private int _next;
    private int _rollback = -1;
    public Exception? Failure { get; private set; }
    public Exception? RollbackFailure { get; private set; }
    public bool Complete { get; private set; }

    public DataPackActivation(int count, Action<int> apply, Action<int> undo)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _count = count;
        _apply = apply;
        _undo = undo;
        Complete = count == 0;
    }

    public void Advance(Func<double> elapsedMilliseconds, bool synchronous = false)
    {
        double start = elapsedMilliseconds();
        int operations = 0;
        while (!Complete && (synchronous || operations == 0
            || (operations < ItemsPerFrame && elapsedMilliseconds() - start < MillisecondsPerFrame)))
        {
            operations++;
            if (Failure == null)
            {
                int index = _next++;
                try { _apply(index); }
                catch (Exception e) { Failure = e; _rollback = index; }
                if (Failure == null && _next == _count) Complete = true;
            }
            else
            {
                try { _undo(_rollback); }
                catch (Exception e) { RollbackFailure ??= e; }
                if (--_rollback < 0) Complete = true;
            }
        }
    }
}
