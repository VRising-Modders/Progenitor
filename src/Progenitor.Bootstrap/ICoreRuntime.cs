using BepInEx.Logging;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime services Bootstrap hands Core at <c>Install</c>. Log source, process role,
/// and the clone/tombstone snapshot that must survive a collectible Core ALC swap.
/// </summary>
public interface ICoreHost
{
    /// <summary>Log source named <c>Progenitor</c> so existing grep lines keep matching.</summary>
    ManualLogSource Log { get; }

    /// <summary>Same rule as Core: dedicated and hosted servers are both <c>VRisingServer*</c>.</summary>
    bool IsServerProcess { get; }

    /// <summary>
    /// Cloned / appended / tombstone GUID sets plus ItemKeyLookup indices. Core copies in
    /// on Install and out before Unload so <c>WasClonedHere</c> survives the ALC swap and
    /// rehydrate can refuse a GUID that is no longer at its saved slot.
    /// </summary>
    ContentMemory ContentMemory { get; }
}

/// <summary>
/// Contract Bootstrap invokes on Core after <c>LoadFromStream</c> into a collectible ALC.
/// Core exposes matching <c>static</c> methods on <c>Progenitor.Runtime</c> (Bootstrap must
/// not compile-reference Core, or loading Bootstrap would pin Core in the default ALC).
/// </summary>
public interface ICoreRuntime
{
    /// <summary>Wire Core modules and attach to the host. Must be safe to call once per load.</summary>
    void Install(ICoreHost host);

    /// <summary>Reverse <see cref="Install"/>. Must not Dispose process-lifetime native detours.</summary>
    void Uninstall();
}

/// <summary>Default <see cref="ICoreHost"/> Bootstrap passes into Core.Install.</summary>
internal sealed class CoreHost : ICoreHost
{
    public CoreHost(ManualLogSource log, bool isServerProcess, ContentMemory contentMemory)
    {
        Log = log ?? throw new System.ArgumentNullException(nameof(log));
        IsServerProcess = isServerProcess;
        ContentMemory = contentMemory ?? throw new System.ArgumentNullException(nameof(contentMemory));
    }

    public ManualLogSource Log { get; }
    public bool IsServerProcess { get; }
    public ContentMemory ContentMemory { get; }
}
