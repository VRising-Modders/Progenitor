using System;
using Unity.Entities;

namespace Progenitor.Hooks;

/// <summary>
/// The world and system identity for one Before/After invocation. Created per dispatch so nested
/// system updates (a group updating a child) cannot clobber each other's Skip flag.
/// </summary>
public sealed class SystemHookContext
{
    private bool _skip;
    private readonly bool _allowSkip;

    internal SystemHookContext(World world, Type systemType, string systemName, bool allowSkip)
    {
        World = world;
        SystemType = systemType;
        SystemName = systemName;
        _allowSkip = allowSkip;
    }

    /// <summary>World that owns the system being updated.</summary>
    public World World { get; }

    /// <summary>Managed interop type passed to <see cref="SystemHooks.Before"/> / After.</summary>
    public Type SystemType { get; }

    /// <summary>Short name of <see cref="SystemType"/>, for logs and throttle keys.</summary>
    public string SystemName { get; }

    /// <summary>
    /// When set from a Before hook, the system's own update is skipped this frame (Harmony prefix
    /// returns false). That is how a plugin owns a vanilla system's outputs: suppress it, then
    /// run replacement work in After. Setting this from an After hook is ignored; the update has
    /// already happened.
    /// </summary>
    public bool Skip
    {
        get => _skip;
        set
        {
            if (!_allowSkip)
            {
                SystemHooks.WarnSkipFromAfter(SystemName);
                return;
            }
            _skip = value;
        }
    }
}
