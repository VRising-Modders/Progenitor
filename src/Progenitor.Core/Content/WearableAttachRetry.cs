namespace Progenitor.Content;

/// <summary>Bounds repeated attachment work while still reacting to changed native references.</summary>
internal sealed class WearableAttachRetry
{
    internal const int RetryFrames = 60;
    private bool _failed;
    private ulong _failedReferences;
    private int _failedFrame;

    internal bool ShouldInspect(bool referenceTick, int frame) =>
        !_failed || referenceTick || Due(frame);

    internal bool ShouldAttempt(ulong references, int frame) =>
        !_failed || references != _failedReferences || Due(frame);

    internal void Failed(ulong references, int frame)
    {
        _failed = true;
        _failedReferences = references;
        _failedFrame = frame;
    }

    private bool Due(int frame) => unchecked(frame - _failedFrame) >= RetryFrames;
}
