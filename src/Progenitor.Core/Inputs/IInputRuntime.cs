using Progenitor.Units;

namespace Progenitor.Inputs;

/// <summary>
/// The game half of <see cref="InputDirector"/>: world identity, the local player's entities,
/// positions, network ids and device activity. <c>UnityInputRuntime</c> is the live one; the
/// tests use a fake. All members are called on the main thread.
/// </summary>
internal interface IInputRuntime
{
    /// <summary>Changes with every client world (epoch); 0 with no client world.</summary>
    long WorldToken { get; }

    /// <summary>Seconds, monotonic.</summary>
    double Now { get; }

    /// <summary>Unity frame number; the director runs lease callbacks once per frame.</summary>
    int Frame { get; }

    bool IsMainThread { get; }

    bool Focused { get; }

    /// <summary>The single input controller entity (holds the command buffer) and the single local character.</summary>
    bool TryGetLocalPlayer(out InputEntityRef controller, out InputEntityRef character);

    /// <summary>A client-world entity's position.</summary>
    bool TryGetPosition(InputEntityRef entity, out UnitVec3 position);

    /// <summary>A client-world entity's <c>NetworkId</c>.</summary>
    bool TryGetNetworkId(InputEntityRef entity, out InputNetworkId id);

    /// <summary>The user's keyboard and mouse this frame. Reads only; never consumes input.</summary>
    UserActivity SampleActivity();

    void Log(string message);

    void Warn(string message);
}
