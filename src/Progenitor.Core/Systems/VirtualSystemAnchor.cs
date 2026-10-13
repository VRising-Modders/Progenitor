using System;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Systems;

/// <summary>
/// Where a virtual system sits relative to a vanilla ECS system. This is a named callback on
/// <see cref="Progenitor.Hooks.SystemHooks"/>, not an injected <c>SystemBase</c>: reload is
/// dispose, and there is no TypeManager row. Default is after
/// <see cref="SimulationSystemGroup"/> (the server and client sim group on this build; there is
/// no <c>ProjectM.ServerSimulationSystemGroup</c>). Sit before/after a specific vanilla system
/// with <see cref="After(Type, Side)"/> / <see cref="Before(Type, Side)"/> when work must land
/// between two children of a group — that is the intra-group seam; group-After is after every child.
/// </summary>
public readonly struct VirtualSystemAnchor : IEquatable<VirtualSystemAnchor>
{
    private readonly Type _systemType;
    private readonly bool _before;
    private readonly Side _side;
    private readonly bool _defaultSimulation;

    private VirtualSystemAnchor(Type systemType, bool before, Side side, bool defaultSimulation)
    {
        _systemType = systemType ?? throw new ArgumentNullException(nameof(systemType));
        _before = before;
        _side = side == 0 ? Side.Both : side;
        _defaultSimulation = defaultSimulation;
    }

    /// <summary>
    /// After <see cref="SimulationSystemGroup"/> on whichever of Server / Client the
    /// <see cref="Side"/> filter allows. If that group's <c>Update</c> never runs through
    /// <c>SystemBase.Update</c> (open research question), Core falls back to After
    /// <c>ServerBootstrapSystem</c> / <c>ClientBootstrapSystem</c> after the side's world
    /// exists plus <see cref="VirtualSystems.DefaultAnchorFallbackTicks"/> Plugin.Frame ticks.
    /// A later group tick switches back; boot frames before WorldReady do not count.
    /// </summary>
    public static VirtualSystemAnchor AfterSimulation { get; } =
        new(typeof(SimulationSystemGroup), before: false, Side.Both, defaultSimulation: true);

    /// <summary>Before <see cref="SimulationSystemGroup"/>, with the same fallback as <see cref="AfterSimulation"/>.</summary>
    public static VirtualSystemAnchor BeforeSimulation { get; } =
        new(typeof(SimulationSystemGroup), before: true, Side.Both, defaultSimulation: true);

    /// <summary>After <paramref name="systemType"/> updates, on worlds matching <paramref name="side"/>.</summary>
    public static VirtualSystemAnchor After(Type systemType, Side side = Side.Both)
        => new(systemType, before: false, side, defaultSimulation: false);

    /// <summary>Before <paramref name="systemType"/> updates, on worlds matching <paramref name="side"/>.</summary>
    public static VirtualSystemAnchor Before(Type systemType, Side side = Side.Both)
        => new(systemType, before: true, side, defaultSimulation: false);

    /// <summary>After <typeparamref name="TSystem"/> updates. <typeparamref name="TSystem"/> must be a game SystemBase or ISystem.</summary>
    public static VirtualSystemAnchor After<TSystem>(Side side = Side.Both)
        => After(typeof(TSystem), side);

    /// <summary>Before <typeparamref name="TSystem"/> updates. <typeparamref name="TSystem"/> must be a game SystemBase or ISystem.</summary>
    public static VirtualSystemAnchor Before<TSystem>(Side side = Side.Both)
        => Before(typeof(TSystem), side);

    /// <summary>Game system this callback is hooked Before/After. For the default simulation anchors this is <see cref="SimulationSystemGroup"/>.</summary>
    public Type SystemType => _systemType;

    /// <summary>True when this is a Before hook; false when After.</summary>
    public bool IsBefore => _before;

    /// <summary>
    /// Worlds this callback may run in. <see cref="Side.Both"/> still only matches the process's
    /// <c>"Server"</c> / <c>"Client_0"</c> world, never a default or editor world.
    /// </summary>
    public Side Side => _side;

    /// <summary>True for <see cref="AfterSimulation"/> / <see cref="BeforeSimulation"/> (the fallback-watched pair).</summary>
    public bool IsDefaultSimulation => _defaultSimulation;

    /// <summary>Short label for logs and <see cref="VirtualSystems.Describe"/>.</summary>
    public string DisplayName
    {
        get
        {
            string phase = _before ? "Before" : "After";
            string typeName = _systemType?.Name ?? "?";
            string side = _side == Side.Both ? "Both" : _side.ToString();
            return $"{phase} {typeName} ({side})";
        }
    }

    /// <summary>Copy with a different world filter, keeping the default-simulation identity so fallback still applies.</summary>
    public VirtualSystemAnchor WithSide(Side side)
        => new(_systemType, _before, side, _defaultSimulation);

    internal string DispatcherKey
    {
        get
        {
            if (_defaultSimulation)
                return _before ? VirtualSystems.DefaultBeforeKey : VirtualSystems.DefaultAfterKey;
            return VirtualSystems.ExplicitKey(_before, _systemType);
        }
    }

    /// <inheritdoc />
    public bool Equals(VirtualSystemAnchor other)
        => _systemType == other._systemType
           && _before == other._before
           && _side == other._side
           && _defaultSimulation == other._defaultSimulation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VirtualSystemAnchor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(_systemType, _before, _side, _defaultSimulation);

    /// <inheritdoc />
    public override string ToString() => DisplayName;

    /// <summary>Value equality.</summary>
    public static bool operator ==(VirtualSystemAnchor left, VirtualSystemAnchor right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(VirtualSystemAnchor left, VirtualSystemAnchor right) => !left.Equals(right);
}
