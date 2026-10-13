namespace Progenitor.Castles;

/// <summary>
/// A castle heart appeared on the server that the previous poll did not see. Published on the
/// main thread through <c>Events.EventBus</c>, so plugin <c>before</c>/<c>after</c>/priority
/// ordering is the same as every other Progenitor event.
///
/// <para>This is a <b>change detection</b>, not a game callback: it fires up to
/// <see cref="CastleWatch.DefaultIntervalSeconds"/> after the placement, and it also fires for
/// hearts that come into existence with the world (the first poll after a save load reports
/// every existing castle once). Subscribe on <c>WorldReady</c> if you only want later changes.</para>
/// </summary>
public sealed class CastleHeartPlaced
{
    /// <summary>Handle for the new castle.</summary>
    public ICastleHandle Castle { get; }

    /// <summary>True when this is the first poll of a world, so every existing heart is reported.</summary>
    public bool InitialScan { get; }

    /// <summary>Construct the event.</summary>
    public CastleHeartPlaced(ICastleHandle castle, bool initialScan)
    {
        Castle = castle;
        InitialScan = initialScan;
    }

    /// <inheritdoc />
    public override string ToString() =>
        "CastleHeartPlaced " + Castle + (InitialScan ? " (initial scan)" : "");
}

/// <summary>
/// A castle heart the previous poll saw is gone (raided, dismantled, decayed, or the world was
/// torn down). Carries the last snapshot: the entity no longer exists, so there is no handle to
/// read from.
/// </summary>
public sealed class CastleHeartDestroyed
{
    /// <summary>Last snapshot of the castle before it disappeared.</summary>
    public CastleSnapshot Castle { get; }

    /// <summary>Heart entity that is gone.</summary>
    public CastleId Heart => Castle.Heart;

    /// <summary>Territory the castle held.</summary>
    public int TerritoryIndex => Castle.TerritoryIndex;

    /// <summary>Owner recorded on the last snapshot.</summary>
    public CastleOwnerInfo Owner => Castle.Owner;

    /// <summary>Construct the event.</summary>
    public CastleHeartDestroyed(CastleSnapshot castle) =>
        Castle = castle ?? new CastleSnapshot();

    /// <inheritdoc />
    public override string ToString() => "CastleHeartDestroyed " + Castle;
}
