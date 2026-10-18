#if !PROGENITOR_TESTS
using System;
using Progenitor.Effects;

namespace Progenitor.Models;

/// <summary>An isolated, static model preview. Dispose closes the surface; its ModelHandle keeps the source loaded.</summary>
public sealed class ModelPreview : IDisposable
{
    private UnityModelPreview? _surface;
    internal ModelPreview(UnityModelPreview surface) => _surface = surface;
    public bool IsOpen => _surface != null && !_surface.IsDisposed;
    public string? CloneName => IsOpen ? _surface?.CloneName : null;
    public void Dispose()
    {
        var surface = _surface;
        _surface = null;
        surface?.Dispose();
    }
}
#endif
