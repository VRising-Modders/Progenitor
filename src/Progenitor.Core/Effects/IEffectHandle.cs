using System;

namespace Progenitor.Effects;

/// <summary>
/// Borrowed vanilla asset. Core-owned so Unity never holds a plugin-implemented
/// <c>Graphic</c> or <c>MonoBehaviour</c> (a collectible vtable on a DontDestroyOnLoad GO
/// kills the client; evidence 36). Dispose releases exactly the objects this handle created.
/// Double-dispose is a no-op.
/// </summary>
public interface IEffectHandle : IDisposable
{
    /// <summary>Family this handle was borrowed as.</summary>
    AssetKind Kind { get; }

    /// <summary>Vanilla name (sprite, ParticleSystem GO, volume label, or buff prefab).</summary>
    string Name { get; }

    /// <summary>
    /// BepInEx plugin GUID that owns the root. The DontDestroyOnLoad GO is named
    /// <c>progenitor-effects-&lt;pluginId&gt;</c> so unload can destroy that exact root (not a
    /// prefix match) and Core Uninstall can still sweep every <c>progenitor-effects-*</c> leftover.
    /// </summary>
    string PluginId { get; }

    /// <summary>DontDestroyOnLoad root this object lives under.</summary>
    string RootName { get; }

    /// <summary>
    /// False on the server, when the module is skipped, or when the named source was missing.
    /// Setters become no-ops; Host returns a disabled host. The caller still Disposes it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/>, including a second call.</summary>
    bool IsDisposed { get; }

    /// <summary>Borrow → Host → Dispose. Disabled handles skip Hosted.</summary>
    EffectHandleState State { get; }
}

/// <summary>
/// Overlay canvas or RenderTexture camera created by <see cref="Assets.Host"/>. Dispose
/// releases the RT, camera and canvas the spike's cleanup released; it does not destroy the
/// world clone (evidence 80: the world clone stayed visible while the RT host was up).
/// </summary>
public interface IHostHandle : IDisposable
{
    HostKind Kind { get; }

    bool Enabled { get; }

    bool IsDisposed { get; }

    /// <summary>Unused layer the RT host parked the driver on, or -1 when layers were not moved.</summary>
    int HostedLayer { get; }
}

/// <summary>Handle lifecycle. Tests assert Borrow → Host → Dispose; double dispose stays Disposed.</summary>
public enum EffectHandleState
{
    /// <summary>Server, skip, or missing source. No Unity object was created.</summary>
    Disabled = 0,

    /// <summary>Client borrow succeeded; not yet hosted.</summary>
    Borrowed = 1,

    /// <summary>A <see cref="IHostHandle"/> is attached.</summary>
    Hosted = 2,

    /// <summary>Released. Further Dispose calls are ignored.</summary>
    Disposed = 3,
}
