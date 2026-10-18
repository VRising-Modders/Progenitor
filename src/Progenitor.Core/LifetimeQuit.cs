using System;

namespace Progenitor;

/// <summary>
/// The one Core callback in Bootstrap's process-lifetime <c>Application.quitting</c> slot
/// (<c>ProcessHost.LifetimeQuitting</c>). The slot holds a single Action, and both Core and the
/// hosted launcher have quit work, so neither points the slot at its own method: Core's quit work
/// (the ConfigSync local-save flush, LOCALSAVE1) runs first, then the hosted launcher's deferred
/// uninstall when it registered one. The slot stays pointed here while either still needs it.
/// </summary>
internal static class LifetimeQuit
{
    private static readonly Action Dispatch = Invoke;
    private static bool _core;

    /// <summary>The hosted launcher's quit callback, or null. Set and cleared by that module.</summary>
    internal static Action? Hosted { get; private set; }

    /// <summary>Core installed: point the slot here.</summary>
    internal static void AttachCore()
    {
        _core = true;
        Point();
    }

    /// <summary>Core uninstalled: the slot stays only while the hosted launcher still has quit work.</summary>
    internal static void DetachCore()
    {
        _core = false;
        Point();
    }

    /// <summary>The hosted launcher registers (non-null) or drops (null) its quit callback.</summary>
    internal static void SetHosted(Action? callback)
    {
        Hosted = callback;
        Point();
    }

    private static void Point() =>
        BootstrapLink.SetLifetimeQuitting(_core || Hosted != null ? Dispatch : null);

    private static void Invoke()
    {
        if (_core)
        {
            try { Networking.ConfigSync.FlushLocalSaves("process exit"); }
            catch { /* the process is going down */ }
        }

        var hosted = Hosted;
        if (hosted == null) return;
        try { hosted(); }
        catch { /* the hosted callback logs its own failures */ }
    }
}
