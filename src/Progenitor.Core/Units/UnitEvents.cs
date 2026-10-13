namespace Progenitor.Units;

/// <summary>
/// The unit reached its GoTo goal (3D distance inside the ApproachPosition stop band)
/// or closed to follow range. Published on the main thread through <see cref="Events.EventBus"/>
/// so plugin <c>before</c>/<c>after</c>/priority order is the same as every other event.
/// </summary>
public sealed class UnitArrived
{
    public IUnitHandle Handle { get; }
    public UnitVec3 Position { get; }

    public UnitArrived(IUnitHandle handle, UnitVec3 position)
    {
        Handle = handle;
        Position = position;
    }
}

/// <summary>
/// The unit cannot complete the current Follow / GoTo (entity gone, write refused, or
/// follow target missing). Same bus as <see cref="UnitArrived"/>.
/// </summary>
public sealed class UnitUnreachable
{
    public IUnitHandle Handle { get; }
    public UnitVec3 Goal { get; }
    public string Reason { get; }

    public UnitUnreachable(IUnitHandle handle, UnitVec3 goal, string reason)
    {
        Handle = handle;
        Goal = goal;
        Reason = reason ?? "";
    }
}
